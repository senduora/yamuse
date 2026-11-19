using System.Globalization;
using System.Net;
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
                Assert(fixture.PlaylistReads == 2 && MetadataReads(fixture) == 0,
                    "unconfirmed album context reached metadata lookup");
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
                Assert(fixture.PlaylistReads == 2 && MetadataReads(fixture) == 0,
                    "incomplete or ambiguous embedded album reached metadata lookup");
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
            Assert(fixture.PlaylistReads == 2 && MetadataReads(fixture) == 0,
                "explicit conflicting root album context reached metadata lookup");
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

        await Run("CatalogPlaylistWrites: a newer owned card rejects stale rename and delete before POST", async () =>
        {
            using var fixture = new PlaylistWritesFixture { Revision = 2, OwnedCardRevision = 3 };
            var expected = fixture.Snapshot();
            var rename = await Throws<MusicException>(() => fixture.Api.RenamePlaylistAsync(FakeSession, expected, "Renamed", default));
            var delete = await Throws<MusicException>(() => fixture.Api.DeletePlaylistAsync(FakeSession, expected, default));
            Assert(fixture.Exists && fixture.Writes.Count == 0 && fixture.Changes.Count == 0
                && fixture.ListReads == 2 && fixture.PlaylistReads == 2
                && new[] { rename, delete }.All(error => error.Message.Contains("изменился", StringComparison.Ordinal)),
                "the newer owned card did not block both writes from stale playlist details");
        });

        await Run("CatalogPlaylistWrites: unchanged mutation revision remains unconfirmed without retry or false readback", async () =>
        {
            using var fixture = new PlaylistWritesFixture { AdvanceRevision = false };
            var error = await Throws<MusicException>(() => fixture.Api.InsertPlaylistTracksAsync(FakeSession, fixture.Snapshot(), [new("track-one", "album-one")], 0, default));
            PlaylistWritesAssertUnconfirmed(fixture, error);
            Assert(fixture.Revision == 1 && fixture.PlaylistReads == 1 && fixture.Tracks.Count == 1,
                "non-advancing change response was accepted or followed by misleading readback");
        });

        await Run("CatalogPlaylistWrites: create and rename reject reads older than their acknowledgement", async () =>
        {
            using var created = new PlaylistWritesFixture(exists: false) { ReadbackRevision = 1, Revision = 2 };
            var error = await Throws<MusicException>(() => created.Api.CreatePlaylistAsync(FakeSession, PlaylistWritesFixture.Title, default));
            Assert(error.Message.Contains("итог не подтверждён", StringComparison.Ordinal) && created.Writes.SequenceEqual(["create"])
                && created.PlaylistReads == 1, "creation accepted an older read or retried the write");

            using var renamed = new PlaylistWritesFixture { Revision = 5, ReadbackRevision = 4 };
            error = await Throws<MusicException>(() => renamed.Api.RenamePlaylistAsync(FakeSession, renamed.Snapshot(), "Renamed", default));
            Assert(error.Message.Contains("итог не подтверждён", StringComparison.Ordinal) && renamed.Writes.SequenceEqual(["rename"])
                && renamed.PlaylistReads == 2, "rename accepted an older read with the same title or retried the write");
        });

        await Run("CatalogPlaylistWrites: acknowledged rename shows the later order without borrowing old row metadata", async () =>
        {
            foreach (var (beforeAck, receipt, accepted) in new[]
            {
                (false, PlaylistWriteAckFault.None, true), (true, PlaylistWriteAckFault.None, true),
                (false, PlaylistWriteAckFault.Sparse, false), (true, PlaylistWriteAckFault.RevisionOnly, false)
            })
            {
                using var fixture = new PlaylistWritesFixture { ReadbackRevision = 3, ReverseReadbackOrder = !beforeAck,
                    ReverseRenameOrderBeforeAck = beforeAck, RenameRevisionStep = beforeAck ? 2 : 1,
                    AckFault = receipt, MetadataStatus = HttpStatusCode.ServiceUnavailable };
                fixture.Tracks.AddRange([new("first", "album-one"), new("second", "album-two")]);
                var before = fixture.Snapshot();
                if (!accepted)
                {
                    await Throws<MusicException>(() => fixture.Api.RenamePlaylistAsync(FakeSession, before, "Renamed", default));
                    Assert(fixture.Writes.SequenceEqual(["rename"]) && MetadataReads(fixture) == 0, "unacknowledged changed order cleared the name confirmation guard");
                    continue;
                }
                var shown = await fixture.Api.RenamePlaylistAsync(FakeSession, before, "Renamed", default);
                Assert(shown is { Owner: "12345", Kind: "17", Revision: 3, Title: "Renamed", EditPositionsVerified: false, InheritedMetadataPositions: null }
                    && shown.Tracks.Select(row => row.RequestId).SequenceEqual(["second:album-two", "first:album-one"])
                    && shown.Tracks.All(row => !row.HasTrackMetadata) && fixture.Writes.SequenceEqual(["rename"]),
                    "the valid rename was hidden or the newer order borrowed old positions");
            }
        });

        await Run("CatalogPlaylistWrites: incomplete write responses are reconciled by reads without replay", async () =>
        {
            foreach (var fault in new[] { PlaylistWriteAckFault.Partial, PlaylistWriteAckFault.InvalidJson, PlaylistWriteAckFault.NoResult, PlaylistWriteAckFault.Sparse })
            {
                using var change = new PlaylistWritesFixture { AckFault = fault };
                var inserted = await change.Api.InsertPlaylistTracksAsync(FakeSession, change.Snapshot(), [new("track-one", "album-one")], 0, default);
                Assert(inserted.Revision == 2 && inserted.Tracks.Single().RequestId == "track-one:album-one"
                    && change.Writes.SequenceEqual(["change"]) && change.PlaylistReads == 2, "an interrupted change was replayed or checked without authoritative readback");
            }

            using var wrongRead = new PlaylistWritesFixture { AckFault = PlaylistWriteAckFault.Partial, ReadbackFault = PlaylistWritesReadbackFault.TrackId };
            var error = await Throws<MusicException>(() => wrongRead.Api.InsertPlaylistTracksAsync(FakeSession, wrongRead.Snapshot(), [new("track-one", "album-one")], 0, default));
            PlaylistWritesAssertUnconfirmed(wrongRead, error);

            using var rename = new PlaylistWritesFixture { AckFault = PlaylistWriteAckFault.Sparse };
            var renamed = await rename.Api.RenamePlaylistAsync(FakeSession, rename.Snapshot(), "Renamed", default);
            Assert(renamed.Revision == 2 && renamed.Title == "Renamed" && rename.Writes.SequenceEqual(["rename"]) && rename.PlaylistReads == 2,
                "interrupted rename did not reconcile the exact playlist by readback");

            using var create = new PlaylistWritesFixture(exists: false) { AckFault = PlaylistWriteAckFault.NoResult };
            error = await Throws<MusicException>(() => create.Api.CreatePlaylistAsync(FakeSession, PlaylistWritesFixture.Title, default));
            Assert(error.Message.Contains("итог не подтверждён", StringComparison.Ordinal) && create.Exists && create.Writes.SequenceEqual(["create"])
                && create.PlaylistReads == 0, "creation retried a lost acknowledgement or guessed the new playlist identity");
        });

        await Run("CatalogPlaylistWrites: explicit authorization conflict and JSON rejections never reconcile as committed", async () =>
        {
            foreach (var fault in new[] { PlaylistWriteAckFault.Forbidden, PlaylistWriteAckFault.Conflict, PlaylistWriteAckFault.ExplicitError })
            {
                using var fixture = new PlaylistWritesFixture { AckFault = fault };
                var error = await Throws<MusicException>(() => fixture.Api.InsertPlaylistTracksAsync(FakeSession, fixture.Snapshot(), [new("track-one", "album-one")], 0, default));
                Assert(fixture.Writes.SequenceEqual(["change"]) && fixture.PlaylistReads == 1 && fixture.Revision == 2
                    && !error.Message.Contains("fixture-error-secret", StringComparison.Ordinal)
                    && !error.Message.Contains("итог не подтверждён", StringComparison.Ordinal),
                    "a rejected POST was replayed, reconciled, or displayed its untrusted response");
            }
        });

        await Run("CatalogPlaylistWrites: revision-only acknowledgements confirm a known playlist once by readback", async () =>
        {
            using var change = new PlaylistWritesFixture { AckFault = PlaylistWriteAckFault.RevisionOnly };
            var inserted = await change.Api.InsertPlaylistTracksAsync(FakeSession, change.Snapshot(), [new("track-one", "album-one")], 0, default);
            Assert(inserted.Revision == 2 && inserted.Tracks.Single().RequestId == "track-one:album-one"
                && change.Writes.SequenceEqual(["change"]) && change.PlaylistReads == 2, "revision-only change did not reconcile the exact playlist once");

            using var rename = new PlaylistWritesFixture { AckFault = PlaylistWriteAckFault.RevisionOnly };
            var renamed = await rename.Api.RenamePlaylistAsync(FakeSession, rename.Snapshot(), "Renamed", default);
            Assert(renamed.Revision == 2 && renamed.Title == "Renamed" && rename.Writes.SequenceEqual(["rename"]) && rename.PlaylistReads == 2,
                "revision-only rename did not reconcile the exact playlist once");

            foreach (var fault in new[] { PlaylistWriteAckFault.RevisionWrongOwner, PlaylistWriteAckFault.RevisionWrongKind,
                PlaylistWriteAckFault.RevisionInvalid, PlaylistWriteAckFault.RevisionAhead, PlaylistWriteAckFault.SparseWrongOwner, PlaylistWriteAckFault.SparseWrongKind })
            {
                using var contradiction = new PlaylistWritesFixture { AckFault = fault };
                var error = await Throws<MusicException>(() => contradiction.Api.InsertPlaylistTracksAsync(FakeSession, contradiction.Snapshot(), [new("track-one", "album-one")], 0, default));
                PlaylistWritesAssertUnconfirmed(contradiction, error);
                Assert(contradiction.PlaylistReads == (fault == PlaylistWriteAckFault.RevisionAhead ? 2 : 1),
                    "contradictory identity or revision was accepted or retried");
            }
        });

        await Run("CatalogPlaylistWrites: invalid components cannot collide in a playlist reference", async () =>
        {
            foreach (var reference in new[] { new LikedId("track-one:album-one", null), new("track,one", "album-one"),
                new("track-one", "album one"), new("track-one", "album:one"), new("track-one", "album,one") })
            {
                using var fixture = new PlaylistWritesFixture();
                await Throws<MusicException>(() => fixture.Api.InsertPlaylistTracksAsync(FakeSession, fixture.Snapshot(), [reference], 0, default));
                Assert(fixture.Http.Seen.Count == 0, "invalid outgoing playlist reference reached HTTP");
                fixture.Tracks.Add(reference);
                await Throws<MusicException>(() => fixture.Api.GetPlaylistAsync(FakeSession, "12345", "17", default));
                Assert(fixture.PlaylistReads == 1 && MetadataReads(fixture) == 0, "invalid incoming playlist reference reached metadata lookup");
            }

            using var collision = new PlaylistWritesFixture { ReadbackFault = PlaylistWritesReadbackFault.CollapsedReference };
            var error = await Throws<MusicException>(() => collision.Api.InsertPlaylistTracksAsync(FakeSession, collision.Snapshot(), [new("track-one", "album-one")], 0, default));
            PlaylistWritesAssertUnconfirmed(collision, error);
            Assert(collision.PlaylistReads == 2 && MetadataReads(collision) == 0,
                "a composite placed in the track ID field confirmed a different playlist reference");
        });

        await Run("CatalogPlaylistWrites: confirmed insert and remove keep exact order when track metadata is temporarily unavailable", async () =>
        {
            using var fixture = new PlaylistWritesFixture { AckFault = PlaylistWriteAckFault.Sparse, MetadataStatus = HttpStatusCode.ServiceUnavailable };
            fixture.Tracks.AddRange([new("repeated", "album-one"), new("other-track", "album-two"), new("repeated", "album-one")]);
            var raw = await fixture.Api.GetPlaylistForEditingAsync(FakeSession, "12345", "17", default);
            Assert(raw.Tracks.All(row => !row.HasTrackMetadata) && fixture.PlaylistReads == 1 && MetadataReads(fixture) == 0,
                "the editing snapshot already claimed track metadata or tried to fetch it");
            var inserted = await fixture.Api.InsertPlaylistTracksAsync(FakeSession, raw, [new("track-one", "album-one")], 1, default);
            Assert(inserted.Revision == 2 && inserted.Tracks.Select(row => row.RequestId)
                    .SequenceEqual(["repeated:album-one", "track-one:album-one", "other-track:album-two", "repeated:album-one"])
                && inserted.Tracks.Select(row => row.Number).SequenceEqual([1, 2, 3, 4])
                && inserted.Tracks.All(row => !row.HasTrackMetadata && !row.Available)
                && fixture.PlaylistReads == 3 && MetadataReads(fixture) == 1 && fixture.Writes.SequenceEqual(["change"]),
                "optional metadata invalidated a committed insertion or changed exact occurrences");

            var removed = await fixture.Api.RemovePlaylistTrackAsync(FakeSession, inserted, 0, "repeated:album-one", default);
            Assert(removed.Revision == 3 && removed.Tracks.Select(row => row.RequestId)
                    .SequenceEqual(["track-one:album-one", "other-track:album-two", "repeated:album-one"])
                && removed.Tracks.Select(row => row.Number).SequenceEqual([1, 2, 3])
                && removed.Tracks.All(row => !row.HasTrackMetadata && !row.Available)
                && fixture.PlaylistReads == 5 && MetadataReads(fixture) == 2 && fixture.Writes.SequenceEqual(["change", "change"]),
                "the authoritative snapshot was not usable to remove one exact occurrence");

            using var denied = new PlaylistWritesFixture { MetadataStatus = HttpStatusCode.Unauthorized };
            var confirmed = await denied.Api.InsertPlaylistTracksAsync(FakeSession, denied.Snapshot(), [new("track-one", "album-one")], 0, default);
            Assert(confirmed.Revision == 2 && confirmed.MetadataIssue == PlaylistMetadataIssue.Authentication
                && confirmed.Tracks.Single() is { RequestId: "track-one:album-one", HasTrackMetadata: false }
                && denied.Writes.SequenceEqual(["change"]) && MetadataReads(denied) == 1,
                "an authentication failure in optional metadata invalidated the confirmed change or lost the separate issue");
        });

        await Run("CatalogPlaylistWrites: editing snapshot is authoritative and a confirmed rename survives unavailable metadata", async () =>
        {
            using var fixture = new PlaylistWritesFixture { MetadataStatus = HttpStatusCode.ServiceUnavailable };
            fixture.Tracks.AddRange([new("repeated", "album-one"), new("repeated", "album-one")]);
            var raw = await fixture.Api.GetPlaylistForEditingAsync(FakeSession, "12345", "17", default);
            Assert(raw.Revision == 1 && raw.Tracks.Select(row => row.RequestId).SequenceEqual(["repeated:album-one", "repeated:album-one"])
                && raw.Tracks.Select(row => row.Number).SequenceEqual([1, 2]) && raw.Tracks.All(row => !row.HasTrackMetadata)
                && fixture.PlaylistReads == 1 && MetadataReads(fixture) == 0,
                "loading a snapshot for editing required optional metadata or lost precise occurrences");
            var knownRows = raw.Tracks.Select(row => new TrackRow
            {
                Id = row.Id, RequestId = row.RequestId, Number = row.Number, AlbumId = "album-one", Title = "Known track", Available = true
            }).ToArray();
            var renamed = await fixture.Api.RenamePlaylistAsync(FakeSession, raw with { Tracks = knownRows }, "New title", default);
            Assert(renamed.Title == "New title" && renamed.Revision == 2 && renamed.Tracks.SequenceEqual(knownRows)
                && renamed.EditPositionsVerified && renamed.InheritedMetadataPositions?.SetEquals([0, 1]) == true
                && fixture.PlaylistReads == 3 && MetadataReads(fixture) == 1 && fixture.Writes.SequenceEqual(["rename"]),
                "temporary metadata loss erased the confirmed rename or replaced exactly known rows");

            using var later = new PlaylistWritesFixture { ReadbackRevision = 4, MetadataStatus = HttpStatusCode.ServiceUnavailable };
            later.Tracks.AddRange([new("repeated", "album-one"), new("repeated", "album-one")]);
            var newer = await later.Api.RenamePlaylistAsync(FakeSession, later.Snapshot() with { Tracks = knownRows }, "New title", default);
            Assert(newer.Revision == 4 && newer.Title == "New title" && !newer.EditPositionsVerified
                && newer.Tracks.All(row => !row.HasTrackMetadata && !row.Available) && later.Writes.SequenceEqual(["rename"]),
                "a later matching title inherited unproven duplicate metadata or replayed rename");

            using var jumped = new PlaylistWritesFixture { RenameRevisionStep = 3, MetadataStatus = HttpStatusCode.ServiceUnavailable };
            jumped.Tracks.AddRange([new("repeated", "album-one"), new("repeated", "album-one")]);
            var acknowledgedJump = await jumped.Api.RenamePlaylistAsync(FakeSession, jumped.Snapshot() with { Tracks = knownRows }, "New title", default);
            Assert(acknowledgedJump.Revision == 4 && acknowledgedJump.Title == "New title" && !acknowledgedJump.EditPositionsVerified
                && acknowledgedJump.InheritedMetadataPositions is null && acknowledgedJump.Tracks.All(row => !row.HasTrackMetadata)
                && jumped.Writes.SequenceEqual(["rename"]),
                "a rename acknowledgement with the same jumped readback revision inherited unproven duplicate metadata");

            using var apiError = new PlaylistWritesFixture { MetadataBody = """{"error":{"name":"metadata-fixture"}}""" };
            apiError.Tracks.Add(new("known", "album-one"));
            var renamedWithoutMetadata = await apiError.Api.RenamePlaylistAsync(FakeSession, apiError.Snapshot(), "Known new name", default);
            Assert(renamedWithoutMetadata.Revision == 2 && renamedWithoutMetadata.Title == "Known new name"
                && renamedWithoutMetadata.MetadataIssue == PlaylistMetadataIssue.Unavailable && apiError.Writes.SequenceEqual(["rename"]),
                "an API error limited to optional metadata erased the confirmed rename");

            using var createdFixture = new PlaylistWritesFixture(exists: false) { MetadataStatus = HttpStatusCode.ServiceUnavailable };
            createdFixture.Tracks.Add(new("preset-track", "album-one"));
            var created = await createdFixture.Api.CreatePlaylistAsync(FakeSession, PlaylistWritesFixture.Title, default);
            Assert(created.Revision == 1 && created.Tracks.Single() is { RequestId: "preset-track:album-one", HasTrackMetadata: false }
                && createdFixture.PlaylistReads == 1 && MetadataReads(createdFixture) == 1 && createdFixture.Writes.SequenceEqual(["create"]),
                "temporary metadata loss discarded the confirmed creation");
        });

        await Run("CatalogPlaylistWrites: sparse metadata preserves only the mapped surviving occurrences", async () =>
        {
            using var fixture = new PlaylistWritesFixture();
            fixture.Tracks.AddRange([new("first", "album-one"), new("repeated", "album-one"),
                new("repeated", "album-one"), new("edge", "album-one")]);
            var titles = new[] { "First", "First occurrence", "Second occurrence", "Known available" };
            var known = fixture.Tracks.Select((reference, index) => new TrackRow
            {
                Id = reference.Id, RequestId = reference.RequestId, Number = index + 1, Title = titles[index],
                DurationMs = 900 + index, Available = true, CoverUri = $"https://avatars.yandex.net/fixture-{index}/%%"
            }).ToArray();
            fixture.OmittedMetadataIds.Add("repeated");
            fixture.UnavailableMetadataIds.Add("edge");
            var renamed = await fixture.Api.RenamePlaylistAsync(FakeSession, fixture.Snapshot() with { Tracks = known }, "Renamed", default);
            Assert(renamed is { Title: "Renamed", Revision: 2 } && ReferenceEquals(renamed.Tracks[1], known[1])
                && ReferenceEquals(renamed.Tracks[2], known[2]) && renamed.Tracks[2].Available
                && renamed.Tracks[3].HasTrackMetadata && !renamed.Tracks[3].Available,
                "sparse successful metadata discarded exact known rows or restored explicitly unavailable audio");

            fixture.MetadataStatus = HttpStatusCode.ServiceUnavailable;
            var removed = await fixture.Api.RemovePlaylistTrackAsync(FakeSession, renamed, 1, "repeated:album-one", default);
            Assert(removed.Revision == 3 && removed.Tracks[1] is { Title: "Second occurrence", Number: 2, DurationMs: 902, Available: true }
                && removed.Tracks[1].CoverUri == known[2].CoverUri && known[2].Number == 3 && !removed.Tracks[2].Available,
                "deleting the first duplicate borrowed its metadata or mutated the previous occurrence number");

            fixture.MetadataStatus = HttpStatusCode.OK;
            fixture.OmittedMetadataIds.UnionWith(["first", "edge"]);
            var conflicting = new TrackRow { Id = "repeated", RequestId = "repeated:other-album", Title = "Wrong edition", Available = true };
            var inserted = await fixture.Api.InsertPlaylistTracksAsync(FakeSession, removed, [new("repeated", "album-one")], 1, default, [conflicting]);
            Assert(inserted.Revision == 4 && inserted.Tracks.Select(row => row.RequestId).SequenceEqual(
                    ["first:album-one", "repeated:album-one", "repeated:album-one", "edge:album-one"])
                && inserted.Tracks[1] is { HasTrackMetadata: false, Number: 2 }
                && inserted.Tracks[2] is { HasTrackMetadata: true, Title: "Second occurrence", Number: 3 }
                && inserted.Tracks.Select(row => row.Number).SequenceEqual([1, 2, 3, 4])
                && fixture.Writes.SequenceEqual(["rename", "change", "change"]) && fixture.PlaylistReads == 6 && MetadataReads(fixture) == 3,
                "an inserted duplicate inherited another occurrence's data, lost order, or repeated a write");

            fixture.OmittedMetadataIds.Add("known-source");
            var source = new TrackRow { Id = "known-source", RequestId = "known-source:album-one", Number = 2,
                Title = "Known command row", DurationMs = 1230, Available = true };
            var added = await fixture.Api.InsertPlaylistTracksAsync(FakeSession, inserted, [new("known-source", "album-one")], 1, default, [source]);
            Assert(added.Revision == 5 && added.Tracks[1] is { Title: "Known command row", DurationMs: 1230, Number: 2, Available: true }
                && added.Tracks[3] is { Title: "Second occurrence", Number: 4, Available: true } && !ReferenceEquals(added.Tracks[1], source)
                && fixture.Writes.SequenceEqual(["rename", "change", "change", "change"]) && fixture.PlaylistReads == 8 && MetadataReads(fixture) == 4,
                "the exact command row or surviving occurrence disappeared from the confirmed order");
        });

        await Run("CatalogPlaylistWrites: only a readback at the acknowledged revision proves the edited occurrences", async () =>
        {
            foreach (var (readbackRevision, acknowledgement, verified) in new[]
            {
                (2, PlaylistWriteAckFault.RevisionOnly, true),
                (4, PlaylistWriteAckFault.RevisionOnly, false),
                (2, PlaylistWriteAckFault.Sparse, false)
            })
            {
                using var fixture = new PlaylistWritesFixture { ReadbackRevision = readbackRevision, AckFault = acknowledgement,
                    MetadataStatus = HttpStatusCode.ServiceUnavailable };
                fixture.Tracks.AddRange([new("repeated", "album-one"), new("repeated", "album-one")]);
                var removed = new TrackRow { Number = 1, Id = "repeated", RequestId = "repeated:album-one", Title = "Removed", Available = true };
                var survivor = new TrackRow { Number = 2, Id = "repeated", RequestId = "repeated:album-one", Title = "Existing survivor", Available = true };
                var edit = await fixture.Api.RemovePlaylistTrackAsync(FakeSession, fixture.Snapshot() with { Tracks = [removed, survivor] },
                    0, removed.RequestId, default);
                var row = edit.Tracks.Single();
                Assert(edit.Revision == readbackRevision && edit.EditPositionsVerified == verified && row.Number == 1
                    && row.RequestId == survivor.RequestId && row.HasTrackMetadata == verified && row.Available == verified
                    && (!verified || row.Title == survivor.Title) && fixture.Writes.SequenceEqual(["change"]),
                    "confirmed end state was replayed or borrowed a duplicate without confirming its edit revision");
            }
        });

        await Run("CatalogPlaylistWrites: ambiguous deletion needs exact 404 and absence from the owned playlist list", async () =>
        {
            foreach (var (status, loss, partial) in new[] { (HttpStatusCode.InternalServerError, false, false),
                (HttpStatusCode.RequestTimeout, false, false), (HttpStatusCode.OK, true, false), (HttpStatusCode.OK, false, true) })
            {
                using var fixture = new PlaylistWritesFixture { DeleteStatus = status, LoseDeleteResponse = loss, DeletePartialResponse = partial };
                await fixture.Api.DeletePlaylistAsync(FakeSession, fixture.Snapshot(), default);
                Assert(!fixture.Exists && fixture.Writes.SequenceEqual(["delete"]) && fixture.PlaylistReads == 2 && fixture.ListReads == 2,
                    "a lost deletion response was replayed or confirmed without checking both server reads");
            }

            foreach (var (status, staleList) in new[] { (HttpStatusCode.OK, false), (HttpStatusCode.ServiceUnavailable, false), (HttpStatusCode.NotFound, true) })
            {
                using var fixture = new PlaylistWritesFixture { DeletePartialResponse = true,
                    DeletedDetailStatus = status, DeletedPlaylistInList = staleList };
                var error = await Throws<MusicException>(() => fixture.Api.DeletePlaylistAsync(FakeSession, fixture.Snapshot(), default));
                Assert(error.Message.Contains("итог не подтверждён", StringComparison.Ordinal) && fixture.Writes.SequenceEqual(["delete"])
                    && fixture.PlaylistReads == 2 && fixture.ListReads == (staleList ? 2 : 1),
                    "a missing or contradictory deletion read was reported as confirmed, or the POST was repeated");
            }

            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.Conflict, HttpStatusCode.OK })
            {
                using var fixture = new PlaylistWritesFixture { DeleteStatus = status, DeleteExplicitError = status == HttpStatusCode.OK };
                var error = await Throws<MusicException>(() => fixture.Api.DeletePlaylistAsync(FakeSession, fixture.Snapshot(), default));
                Assert(error.Authentication == (status == HttpStatusCode.Unauthorized) && fixture.Writes.SequenceEqual(["delete"])
                    && fixture.Exists && fixture.PlaylistReads == 1 && fixture.ListReads == 1
                    && !error.Message.Contains("итог не подтверждён", StringComparison.Ordinal),
                    "an explicit deletion rejection was treated as ambiguous or triggered extra requests");
            }
        });
    }

    private static int MetadataReads(PlaylistWritesFixture fixture) => fixture.Http.Seen.Count(call => call.Uri.AbsolutePath == "/tracks");

    private static void PlaylistWritesAssertUnconfirmed(PlaylistWritesFixture fixture, MusicException error)
        => Assert(!error.Authentication && error.Message.Contains("итог не подтверждён", StringComparison.Ordinal)
            && fixture.Changes.Count == 1 && fixture.Writes.SequenceEqual(["change"]), "unconfirmed playlist write was accepted or automatically retried");
    private static Dictionary<string, string> PlaylistWritesForm(string body) => body.Split('&').Select(field => field.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')), StringComparer.Ordinal);
    private enum PlaylistWritesReadbackFault { None, TrackId, MissingAlbumId, WrongAlbumId, CollapsedReference }
    private enum PlaylistWritesEmbeddedTrackShape { None, ExactSingleton, WrongTrackId, MissingTrackId, AbsentAlbums, MultipleAlbums }
    private enum PlaylistWriteAckFault
    {
        None, Partial, InvalidJson, NoResult, Forbidden, Conflict, ExplicitError,
        RevisionOnly, RevisionWrongOwner, RevisionWrongKind, RevisionInvalid, RevisionAhead, Sparse, SparseWrongOwner, SparseWrongKind
    }
    private sealed class PlaylistWritesFixture : IDisposable
    {
        internal const string Title = "Playlist writes fixture";
        internal MusicApi Api { get; }
        internal FakeHandler Http { get; }
        internal bool Exists { get; private set; }
        internal int Revision { get; set; } = 1;
        internal string AccountUid { get; init; } = "12345";
        internal bool AdvanceRevision { get; init; } = true;
        internal int RenameRevisionStep { get; init; } = 1;
        internal bool ReverseRenameOrderBeforeAck { get; init; }
        internal HttpStatusCode MetadataStatus { get; set; } = HttpStatusCode.OK;
        internal string? MetadataBody { get; init; }
        internal HashSet<string> OmittedMetadataIds { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> UnavailableMetadataIds { get; } = new(StringComparer.Ordinal);
        internal HttpStatusCode DeleteStatus { get; init; } = HttpStatusCode.OK;
        internal HttpStatusCode DeletedDetailStatus { get; init; } = HttpStatusCode.NotFound;
        internal bool LoseDeleteResponse { get; init; }
        internal bool DeletePartialResponse { get; init; }
        internal bool DeleteExplicitError { get; init; }
        internal bool DeletedPlaylistInList { get; init; }
        internal int? ReadbackRevision { get; init; }
        internal int? OwnedCardRevision { get; init; }
        internal bool ReverseReadbackOrder { get; init; }
        internal PlaylistWriteAckFault AckFault { get; init; }
        internal PlaylistWritesReadbackFault ReadbackFault { get; init; }
        internal PlaylistWritesEmbeddedTrackShape EmbeddedTrackShape { get; init; }
        internal string EmbeddedAlbumId { get; init; } = "album-one";
        internal List<LikedId> Tracks { get; } = [];
        internal List<(int Revision, string Diff)> Changes { get; } = [];
        internal List<string> Writes { get; } = [];
        internal int PlaylistReads { get; private set; }
        internal int ListReads { get; private set; }
        private string _title = Title;
        internal PlaylistWritesFixture(bool exists = true) { Exists = exists; Http = new FakeHandler(Respond); Api = new MusicApi(Http); }
        internal PlaylistDetails Snapshot() => new("12345", "17", _title, "", Revision,
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
                ListReads++;
                var header = Header();
                if (OwnedCardRevision is int revision) header["revision"] = revision;
                return Json(JsonSerializer.Serialize(new { result = Exists || DeletedPlaylistInList ? new[] { header } : Array.Empty<Dictionary<string, object?>>() }));
            }
            if (path == "/users/12345/playlists/17")
            {
                Assert(request.Method == HttpMethod.Get, "playlist detail preflight/readback differs");
                PlaylistReads++;
                if (!Exists && DeletedDetailStatus != HttpStatusCode.OK) return new HttpResponseMessage(DeletedDetailStatus);
                return Json(JsonSerializer.Serialize(new { result = Header(readback: Writes.Count > 0) }));
            }
            if (path == "/tracks")
            {
                Assert(request.Method == HttpMethod.Post, "metadata batching method changed");
                if (MetadataStatus != HttpStatusCode.OK) return Json("{}", MetadataStatus);
                if (MetadataBody is not null) return Json(MetadataBody);
                var body = PlaylistWritesForm(await request.Content!.ReadAsStringAsync(cancellationToken));
                // Metadata always contains the expected album. It must never repair
                // an absent/different albumId in authoritative playlist references.
                var rows = body["track-ids"].Split(',').Select(reference => reference.Split(':')[0]).Where(id => !OmittedMetadataIds.Contains(id)).Select(id => new
                {
                    id, title = "Fixture track", available = !UnavailableMetadataIds.Contains(id),
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
                Exists = true; Writes.Add("create"); return Acknowledge();
            }
            if (path == "/users/12345/playlists/17/name")
            {
                var form = PlaylistWritesForm(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert(Exists && form.Count == 1, "unexpected playlist rename");
                if (ReverseRenameOrderBeforeAck) Tracks.Reverse();
                _title = form["value"]; Revision += RenameRevisionStep; Writes.Add("rename"); return Acknowledge();
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
                return Acknowledge();
            }
            if (path == "/users/12345/playlists/17/delete")
            {
                Assert(Exists, "playlist delete was repeated");
                Writes.Add("delete");
                if (DeleteExplicitError || DeleteStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Conflict)
                    return Json("""{"error":{"name":"fixture-rejected"}}""", DeleteStatus);
                Exists = false;
                if (LoseDeleteResponse) throw new HttpRequestException("fixture response lost after deletion");
                if (DeletePartialResponse) return Json("""{"result":{}}""");
                return Json("""{"result":"ok"}""", DeleteStatus);
            }
            throw new InvalidOperationException("Unexpected playlist write fixture endpoint " + path);
        }
        private HttpResponseMessage Acknowledge()
        {
            var response = Json(AckFault switch
            {
                PlaylistWriteAckFault.InvalidJson => "{",
                PlaylistWriteAckFault.NoResult => "{}",
                PlaylistWriteAckFault.ExplicitError => """{"error":{"message":"fixture-error-secret"}}""",
                PlaylistWriteAckFault.RevisionOnly => JsonSerializer.Serialize(new { result = new { revision = Revision } }),
                PlaylistWriteAckFault.RevisionWrongOwner => JsonSerializer.Serialize(new { result = new { revision = Revision, owner = new { uid = "67890" } } }),
                PlaylistWriteAckFault.RevisionWrongKind => JsonSerializer.Serialize(new { result = new { revision = Revision, kind = "18" } }),
                PlaylistWriteAckFault.RevisionInvalid => """{"result":{"revision":-1}}""",
                PlaylistWriteAckFault.RevisionAhead => JsonSerializer.Serialize(new { result = new { revision = Revision + 1 } }),
                PlaylistWriteAckFault.Sparse => """{"result":{}}""",
                PlaylistWriteAckFault.SparseWrongOwner => """{"result":{"owner":{"uid":"67890"}}}""",
                PlaylistWriteAckFault.SparseWrongKind => """{"result":{"kind":18}}""",
                _ => JsonSerializer.Serialize(new { result = Header() })
            });
            response.StatusCode = AckFault switch
            {
                PlaylistWriteAckFault.Partial => HttpStatusCode.PartialContent,
                PlaylistWriteAckFault.Forbidden => HttpStatusCode.Forbidden,
                PlaylistWriteAckFault.Conflict => HttpStatusCode.Conflict,
                _ => HttpStatusCode.OK
            };
            return response;
        }
        private Dictionary<string, object?> Header(bool readback = false) => new()
        {
            ["kind"] = "17", ["owner"] = new { uid = "12345" }, ["title"] = _title, ["description"] = "",
            ["revision"] = readback ? ReadbackRevision ?? Revision : Revision, ["visibility"] = "private", ["trackCount"] = Tracks.Count,
            ["tracks"] = (readback && ReverseReadbackOrder ? Tracks.AsEnumerable().Reverse() : Tracks).Select(reference =>
            {
                if (readback && ReadbackFault == PlaylistWritesReadbackFault.CollapsedReference)
                    return new Dictionary<string, object?> { ["id"] = reference.RequestId };
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
