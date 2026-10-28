using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckCatalogPlaylistWrites()
    {
        await Run("CatalogPlaylistWrites: create insert camel albumId readback remove exclusive range and delete", async () =>
        {
            using var fixture = new PlaylistWritesFixture(exists: false);
            var created = await fixture.Api.CreatePlaylistAsync(FakeSession, PlaylistWritesFixture.Title, "private", default);
            Assert(created is { Revision: 1, Tracks.Count: 0, Visibility: "private" }, "fresh playlist create/readback changed its fixture revision or privacy");
            var inserted = await fixture.Api.InsertPlaylistTracksAsync(FakeSession, created, [new("track-one", "album-one")], 0, default);
            Assert(inserted.Revision == 2 && inserted.Tracks.Single().RequestId == "track-one:album-one"
                && inserted.Tracks[0].AlbumId == "album-one" && fixture.Changes.Count == 1,
                "insert did not retain exact track and album context through authoritative readback");
            var insert = fixture.Changes[0];
            using (var diff = JsonDocument.Parse(insert.Diff))
            {
                var operation = diff.RootElement.EnumerateArray().Single();
                var track = operation.GetProperty("tracks").EnumerateArray().Single();
                Assert(insert.Revision == 1 && operation.GetProperty("op").GetString() == "insert"
                    && operation.GetProperty("at").GetInt32() == 0
                    && track.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal).SetEquals(["id", "albumId"])
                    && track.GetProperty("id").GetString() == "track-one" && track.GetProperty("albumId").GetString() == "album-one"
                    && !track.TryGetProperty("album_id", out _), "playlist insert wire keys or optimistic revision differ from the service contract");
            }
            var empty = await fixture.Api.RemovePlaylistTrackAsync(FakeSession, inserted, 0, "track-one:album-one", default);
            Assert(empty is { Revision: 3, Tracks.Count: 0 } && fixture.Changes.Count == 2, "remove did not confirm the new empty revision");
            var remove = fixture.Changes[1];
            using (var diff = JsonDocument.Parse(remove.Diff))
            {
                var operation = diff.RootElement.EnumerateArray().Single();
                Assert(remove.Revision == 2 && operation.GetProperty("op").GetString() == "delete"
                    && operation.GetProperty("from").GetInt32() == 0 && operation.GetProperty("to").GetInt32() == 1,
                    "playlist remove lost the current revision or exclusive upper index");
            }
            await fixture.Api.DeletePlaylistAsync(FakeSession, empty, default);
            Assert(!fixture.Exists && (await fixture.Api.GetPlaylistsAsync(FakeSession, "12345", default)).Count == 0
                && fixture.Writes.SequenceEqual(["create", "change", "change", "delete"]), "playlist lifecycle lost readback or repeated a mutation");
            Assert(fixture.Http.Seen.All(call => call.Uri.Host == "api.music.yandex.net" && call.Authorization == "OAuth " + FakeSession.AccessToken),
                "playlist fixture left the authenticated music API boundary");
        });

        await Run("CatalogPlaylistWrites: wrong readback track ID stays unconfirmed without repeating change", async () =>
        {
            using var fixture = new PlaylistWritesFixture { ReadbackFault = PlaylistWritesReadbackFault.TrackId };
            var expected = fixture.Snapshot();
            var error = await Throws<MusicException>(() => fixture.Api.InsertPlaylistTracksAsync(FakeSession, expected, [new("track-one", "album-one")], 0, default));
            PlaylistWritesAssertUnconfirmed(fixture, error);
            Assert(fixture.PlaylistReads == 2 && fixture.Revision == 2 && fixture.Tracks.Single().RequestId == "track-one:album-one"
                && expected is { Revision: 1, Tracks.Count: 0 }, "wrong readback did not exercise the committed write or mutated the caller snapshot");
        });

        await Run("CatalogPlaylistWrites: absent or different readback albumId cannot be inferred from metadata or accepted", async () =>
        {
            foreach (var fault in new[] { PlaylistWritesReadbackFault.MissingAlbumId, PlaylistWritesReadbackFault.WrongAlbumId })
            {
                using var fixture = new PlaylistWritesFixture { ReadbackFault = fault };
                var error = await Throws<MusicException>(() => fixture.Api.InsertPlaylistTracksAsync(FakeSession, fixture.Snapshot(), [new("track-one", "album-one")], 0, default));
                PlaylistWritesAssertUnconfirmed(fixture, error);
                var metadata = fixture.Http.Seen.Single(call => call.Uri.AbsolutePath == "/tracks");
                var requested = PlaylistWritesForm(metadata.Body)["track-ids"];
                Assert(requested == (fault == PlaylistWritesReadbackFault.MissingAlbumId ? "track-one" : "track-one:album-other")
                    && fixture.PlaylistReads == 2, "readback did not preserve the service's actual album context through metadata lookup");
            }
        });

        await Run("CatalogPlaylistWrites: absent root albumId uses an exact embedded track and its single explicit album", async () =>
        {
            using var fixture = new PlaylistWritesFixture
            {
                ReadbackFault = PlaylistWritesReadbackFault.MissingAlbumId,
                EmbeddedTrackShape = PlaylistWritesEmbeddedTrackShape.ExactSingleton
            };
            var inserted = await fixture.Api.InsertPlaylistTracksAsync(FakeSession, fixture.Snapshot(), [new("track-one", "album-one")], 0, default);
            var metadata = fixture.Http.Seen.Single(call => call.Uri.AbsolutePath == "/tracks");
            Assert(inserted.Revision == 2 && inserted.Tracks.Single().RequestId == "track-one:album-one"
                && inserted.Tracks[0].AlbumId == "album-one" && PlaylistWritesForm(metadata.Body)["track-ids"] == "track-one:album-one"
                && fixture.Changes.Count == 1 && fixture.PlaylistReads == 2 && fixture.Writes.SequenceEqual(["change"]),
                "exact embedded playlist album was discarded, replaced by metadata, or caused a repeated write");
        });

        await Run("CatalogPlaylistWrites: mismatched incomplete or ambiguous embedded track cannot confirm missing root albumId", async () =>
        {
            foreach (var shape in new[]
            {
                PlaylistWritesEmbeddedTrackShape.WrongTrackId, PlaylistWritesEmbeddedTrackShape.MissingTrackId,
                PlaylistWritesEmbeddedTrackShape.AbsentAlbums, PlaylistWritesEmbeddedTrackShape.MultipleAlbums
            })
            {
                using var fixture = new PlaylistWritesFixture { ReadbackFault = PlaylistWritesReadbackFault.MissingAlbumId, EmbeddedTrackShape = shape };
                var error = await Throws<MusicException>(() => fixture.Api.InsertPlaylistTracksAsync(FakeSession, fixture.Snapshot(), [new("track-one", "album-one")], 0, default));
                PlaylistWritesAssertUnconfirmed(fixture, error);
                var metadata = fixture.Http.Seen.Single(call => call.Uri.AbsolutePath == "/tracks");
                Assert(fixture.PlaylistReads == 2 && PlaylistWritesForm(metadata.Body)["track-ids"] == "track-one",
                    "incomplete or ambiguous embedded album was guessed despite the authoritative reference remaining incomplete");
            }
        });

        await Run("CatalogPlaylistWrites: explicit different root albumId cannot be replaced by correct embedded album", async () =>
        {
            using var fixture = new PlaylistWritesFixture
            {
                ReadbackFault = PlaylistWritesReadbackFault.WrongAlbumId,
                EmbeddedTrackShape = PlaylistWritesEmbeddedTrackShape.ExactSingleton
            };
            var error = await Throws<MusicException>(() => fixture.Api.InsertPlaylistTracksAsync(FakeSession, fixture.Snapshot(), [new("track-one", "album-one")], 0, default));
            PlaylistWritesAssertUnconfirmed(fixture, error);
            var metadata = fixture.Http.Seen.Single(call => call.Uri.AbsolutePath == "/tracks");
            Assert(fixture.PlaylistReads == 2 && PlaylistWritesForm(metadata.Body)["track-ids"] == "track-one:album-other",
                "explicit root album context was overwritten by the embedded track or later metadata");
        });

        await Run("CatalogPlaylistWrites: invalid embedded singleton album ID stays safely unconfirmed without retry or metadata repair", async () =>
        {
            foreach (var albumId in new[] { " ", "\n", new string('x', 513) })
            {
                using var fixture = new PlaylistWritesFixture
                {
                    ReadbackFault = PlaylistWritesReadbackFault.MissingAlbumId,
                    EmbeddedTrackShape = PlaylistWritesEmbeddedTrackShape.ExactSingleton,
                    EmbeddedAlbumId = albumId
                };
                var error = await Throws<MusicException>(() => fixture.Api.InsertPlaylistTracksAsync(FakeSession, fixture.Snapshot(), [new("track-one", "album-one")], 0, default));
                PlaylistWritesAssertUnconfirmed(fixture, error);
                Assert(fixture.Revision == 2 && fixture.PlaylistReads == 2 && fixture.Tracks.Single().RequestId == "track-one:album-one"
                    && fixture.Http.Seen.All(call => call.Uri.AbsolutePath != "/tracks"),
                    "invalid embedded album bypassed the safe post-write error or was repaired through metadata");
            }
        });

        await Run("CatalogPlaylistWrites: stale revision stops before change and leaves server playlist intact", async () =>
        {
            using var fixture = new PlaylistWritesFixture();
            var expected = fixture.Snapshot(); fixture.Revision = 2;
            var error = await Throws<MusicException>(() => fixture.Api.InsertPlaylistTracksAsync(FakeSession, expected, [new("track-one", "album-one")], 0, default));
            Assert(fixture.Changes.Count == 0 && fixture.Writes.Count == 0 && fixture.Revision == 2 && fixture.Tracks.Count == 0
                && fixture.PlaylistReads == 1 && error.Message.Contains("изменился", StringComparison.OrdinalIgnoreCase),
                "stale playlist snapshot sent a mutation or bypassed the authoritative preflight");
        });

        await Run("CatalogPlaylistWrites: current account must own playlist before any change request", async () =>
        {
            using var fixture = new PlaylistWritesFixture { AccountUid = "other-account" };
            await Throws<MusicException>(() => fixture.Api.InsertPlaylistTracksAsync(FakeSession, fixture.Snapshot(), [new("track-one", "album-one")], 0, default));
            Assert(fixture.Writes.Count == 0 && fixture.Changes.Count == 0 && fixture.PlaylistReads == 0
                && fixture.Http.Seen.Count == 1 && fixture.Http.Seen[0].Uri.AbsolutePath == "/account/status",
                "playlist write bypassed account ownership or continued after a failed owner check");
        });

        await Run("CatalogPlaylistWrites: unchanged mutation revision remains unconfirmed without retry or false readback", async () =>
        {
            using var fixture = new PlaylistWritesFixture { AdvanceRevision = false };
            var error = await Throws<MusicException>(() => fixture.Api.InsertPlaylistTracksAsync(FakeSession, fixture.Snapshot(), [new("track-one", "album-one")], 0, default));
            PlaylistWritesAssertUnconfirmed(fixture, error);
            Assert(fixture.Revision == 1 && fixture.PlaylistReads == 1 && fixture.Tracks.Count == 1,
                "non-advancing change response was accepted or followed by misleading readback");
        });
    }

    private static void PlaylistWritesAssertUnconfirmed(PlaylistWritesFixture fixture, MusicException error)
        => Assert(!error.Authentication && error.Message.Contains("итог не подтверждён", StringComparison.Ordinal)
            && fixture.Changes.Count == 1 && fixture.Writes.SequenceEqual(["change"]), "unconfirmed playlist write was accepted or automatically retried");
    private static Dictionary<string, string> PlaylistWritesForm(string body) => body.Split('&').Select(field => field.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')), StringComparer.Ordinal);
    private enum PlaylistWritesReadbackFault { None, TrackId, MissingAlbumId, WrongAlbumId }
    private enum PlaylistWritesEmbeddedTrackShape { None, ExactSingleton, WrongTrackId, MissingTrackId, AbsentAlbums, MultipleAlbums }
    private sealed class PlaylistWritesFixture : IDisposable
    {
        internal const string Title = "Playlist writes fixture";
        internal MusicApi Api { get; }
        internal FakeHandler Http { get; }
        internal bool Exists { get; private set; }
        internal int Revision { get; set; } = 1;
        internal string AccountUid { get; init; } = "12345";
        internal bool AdvanceRevision { get; init; } = true;
        internal PlaylistWritesReadbackFault ReadbackFault { get; init; }
        internal PlaylistWritesEmbeddedTrackShape EmbeddedTrackShape { get; init; }
        internal string EmbeddedAlbumId { get; init; } = "album-one";
        internal List<LikedId> Tracks { get; } = [];
        internal List<(int Revision, string Diff)> Changes { get; } = [];
        internal List<string> Writes { get; } = [];
        internal int PlaylistReads { get; private set; }
        internal PlaylistWritesFixture(bool exists = true) { Exists = exists; Http = new FakeHandler(Respond); Api = new MusicApi(Http); }
        internal PlaylistDetails Snapshot() => new("12345", "17", Title, "", Revision,
            Tracks.Select(reference => new TrackRow { Id = reference.Id, RequestId = reference.RequestId, AlbumId = reference.AlbumId }).ToArray(), Visibility: "private");
        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert(request.RequestUri!.Host == "api.music.yandex.net" && request.Headers.Authorization?.ToString() == "OAuth " + FakeSession.AccessToken,
                "unexpected playlist fixture host or credentials");
            var path = request.RequestUri.AbsolutePath;
            if (path == "/account/status")
            {
                Assert(request.Method == HttpMethod.Get, "account preflight must be read-only");
                return Json(JsonSerializer.Serialize(new { result = new { account = new { uid = AccountUid, displayName = "Fixture Account" } } }));
            }
            if (path == "/users/12345/playlists/list")
            {
                Assert(request.Method == HttpMethod.Get, "playlist list preflight must be read-only");
                return Json(JsonSerializer.Serialize(new { result = Exists ? new[] { Header() } : Array.Empty<Dictionary<string, object?>>() }));
            }
            if (path == "/users/12345/playlists/17")
            {
                Assert(request.Method == HttpMethod.Get && Exists, "playlist detail preflight/readback differs");
                PlaylistReads++; return Json(JsonSerializer.Serialize(new { result = Header(readback: Changes.Count > 0) }));
            }
            if (path == "/tracks")
            {
                Assert(request.Method == HttpMethod.Post, "metadata batching method changed");
                var body = PlaylistWritesForm(await request.Content!.ReadAsStringAsync(cancellationToken));
                // Metadata always contains the expected album. It must never repair
                // an absent/different albumId in authoritative playlist references.
                var rows = body["track-ids"].Split(',').Select(reference => new
                {
                    id = reference.Split(':')[0], title = "Fixture track", available = true,
                    artists = new[] { new { id = "fixture-artist", name = "Fixture" } },
                    albums = new[] { new { id = "album-one", title = "Fixture album" } }
                });
                return Json(JsonSerializer.Serialize(new { result = rows }));
            }
            Assert(request.Method == HttpMethod.Post, "unexpected non-POST playlist mutation");
            if (path == "/users/12345/playlists/create")
            {
                var form = PlaylistWritesForm(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert(!Exists && form["title"] == Title && form["visibility"] == "private", "unexpected playlist creation");
                Exists = true; Writes.Add("create"); return Json(JsonSerializer.Serialize(new { result = Header() }));
            }
            if (path == "/users/12345/playlists/17/change")
            {
                var form = PlaylistWritesForm(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert(Exists && form.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["kind", "revision", "diff"])
                    && form["kind"] == "17" && int.Parse(form["revision"], CultureInfo.InvariantCulture) == Revision,
                    "change lost its exact form keys or optimistic revision");
                Changes.Add((Revision, form["diff"])); Writes.Add("change");
                using var diff = JsonDocument.Parse(form["diff"]);
                var operation = diff.RootElement.EnumerateArray().Single();
                if (operation.GetProperty("op").GetString() == "insert")
                {
                    var inserted = operation.GetProperty("tracks").EnumerateArray().Select(track =>
                    {
                        // Service wire is camelCase. Unknown snake_case is ignored,
                        // reproducing the live defect instead of granting it an alias.
                        var album = track.TryGetProperty("albumId", out var albumId) ? albumId.GetString() : null;
                        return new LikedId(track.GetProperty("id").GetString()!, album);
                    }).ToArray();
                    Tracks.InsertRange(operation.GetProperty("at").GetInt32(), inserted);
                }
                else
                {
                    Assert(operation.GetProperty("op").GetString() == "delete", "unexpected playlist diff operation");
                    var from = operation.GetProperty("from").GetInt32(); var to = operation.GetProperty("to").GetInt32();
                    Assert(from >= 0 && to > from && to <= Tracks.Count, "invalid exclusive delete range");
                    Tracks.RemoveRange(from, to - from);
                }
                if (AdvanceRevision) Revision++;
                return Json(JsonSerializer.Serialize(new { result = Header() }));
            }
            if (path == "/users/12345/playlists/17/delete")
            {
                Assert(Exists, "playlist delete was repeated"); Exists = false; Writes.Add("delete"); return Json("""{"result":"ok"}""");
            }
            throw new InvalidOperationException("Unexpected playlist write fixture endpoint " + path);
        }
        private Dictionary<string, object?> Header(bool readback = false) => new()
        {
            ["kind"] = "17", ["owner"] = new { uid = "12345" }, ["title"] = Title, ["description"] = "",
            ["revision"] = Revision, ["visibility"] = "private", ["trackCount"] = Tracks.Count,
            ["tracks"] = Tracks.Select(reference =>
            {
                var entry = new Dictionary<string, object?> { ["id"] = readback && ReadbackFault == PlaylistWritesReadbackFault.TrackId ? "wrong-track" : reference.Id };
                var album = readback && ReadbackFault == PlaylistWritesReadbackFault.WrongAlbumId ? "album-other" : reference.AlbumId;
                if (!(readback && ReadbackFault == PlaylistWritesReadbackFault.MissingAlbumId) && album is not null) entry["albumId"] = album;
                if (readback && EmbeddedTrackShape != PlaylistWritesEmbeddedTrackShape.None) entry["track"] = EmbeddedTrack(reference);
                return entry;
            }).ToArray()
        };
        private Dictionary<string, object?> EmbeddedTrack(LikedId reference)
        {
            var track = new Dictionary<string, object?>();
            if (EmbeddedTrackShape != PlaylistWritesEmbeddedTrackShape.MissingTrackId)
                track["id"] = EmbeddedTrackShape == PlaylistWritesEmbeddedTrackShape.WrongTrackId ? "different-embedded-track" : reference.Id;
            if (EmbeddedTrackShape != PlaylistWritesEmbeddedTrackShape.AbsentAlbums)
                track["albums"] = EmbeddedTrackShape == PlaylistWritesEmbeddedTrackShape.MultipleAlbums
                    ? new[] { new { id = EmbeddedAlbumId }, new { id = "album-other" } } : new[] { new { id = EmbeddedAlbumId } };
            return track;
        }
        public void Dispose() => Api.Dispose();
    }
}
