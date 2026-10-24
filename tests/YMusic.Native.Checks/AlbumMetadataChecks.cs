using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckAlbumMetadata037()
    {
        await Run("Album metadata037: exact requested album and first confirmed album keep title/ID paired and playback references", AlbumRequestedMetadata037);
        await Run("Album metadata037: catalog context preserves playback reference while navigation requires returned album metadata", AlbumCatalogMetadata037);
        await Run("Album metadata037: old indexes stay readable and navigation enrichment preserves downloaded snapshot state", AlbumOfflineMetadata037);
    }

    private static async Task AlbumRequestedMetadata037()
    {
        const string metadata="""
            {"result":[
              {"id":"t1","title":"One","available":true,"albums":[{"id":"a1","title":"Same title"},{"id":"a2","title":"Same title"}]},
              {"id":"t2","title":"Two","available":true,"albums":[{"title":"Unidentified"},{"id":203,"title":"Confirmed first"},{"id":204,"title":"Other"}]},
              {"id":"t3","title":"Three","available":true,"albums":[{"title":"Title only"}]},
              {"id":"t4","title":"Four","available":true}
            ]}
            """;
        var handler=new FakeHandler((_,_)=>Task.FromResult(Json(metadata)));
        using var api=new MusicApi(handler);
        var references=new[]{new LikedId("t1","a2"),new LikedId("t1","absent"),new LikedId("t2","missing"),new LikedId("t3","unconfirmed"),new LikedId("t4",null),new LikedId("missing-track","never-confirmed")};
        var rows=await api.GetTracksAsync(FakeSession,references,30,default);
        Assert(rows[0].AlbumId=="a2"&&rows[0].Album=="Same title", "requested ID was guessed from duplicate title");
        Assert(rows[1].AlbumId=="a1"&&rows[1].Album=="Same title", "absent requested album did not fall back to first real returned album");
        Assert(rows[2].AlbumId=="203"&&rows[2].Album=="Confirmed first", "unidentified first entry displaced a confirmed album or ID/title diverged");
        Assert(rows[3].AlbumId is null&&rows[3].Album=="Title only"&&rows[4].AlbumId is null&&rows[4].Album.Length==0, "navigation ID inferred from request suffix or title without metadata");
        Assert(rows[5].AlbumId is null&&!rows[5].Available, "missing track metadata acquired album navigation");
        Assert(rows.Select(x=>x.RequestId).SequenceEqual(references.Select(x=>x.RequestId))&&rows[0].Number==31, "playback composite IDs/order/offset changed");
        Assert(handler.Seen.Count==1&&handler.Seen[0].Uri.AbsolutePath=="/tracks"&&Uri.UnescapeDataString(handler.Seen[0].Body).Contains("track-ids=t1:a2,t1:absent,t2:missing,t3:unconfirmed,t4,missing-track:never-confirmed",StringComparison.Ordinal), "metadata lookup expanded into per-track/name requests");
    }

    private static async Task AlbumCatalogMetadata037()
    {
        var handler=new FakeHandler((request,_)=>Task.FromResult(Json(request.RequestUri!.AbsolutePath switch
        {
            "/albums/a2/with-tracks"=>"""{"result":{"id":"a2","volumes":[[{"id":"track","title":"Fixture","available":true,"albums":[{"id":"a1","title":"First"},{"id":"a2","title":"Selected"}]}]]}}""",
            "/albums/unconfirmed/with-tracks"=>"""{"result":{"id":"unconfirmed","volumes":[[{"id":"track","title":"Fixture","available":true,"albums":[{"id":"a1","title":"Actual returned album"}]}]]}}""",
            "/artists/fixture/tracks"=>"""{"result":{"tracks":[{"id":"track","title":"Fixture","available":true,"albums":[{"id":"a1","title":"First"},{"id":"a2","title":"Selected"}]},{"id":"no-album","title":"No album","available":true}]}}""",
            _=>throw new InvalidOperationException("Unexpected album fixture endpoint")
        })));
        using var api=new MusicApi(handler);
        var album=(await api.GetAlbumTracksAsync(FakeSession,"a2",default)).Single();
        Assert(album.AlbumId=="a2"&&album.Album=="Selected"&&album.RequestId=="track:a2", "catalog context album differs from navigation ID/title or playback changed");
        var unmatched=(await api.GetAlbumTracksAsync(FakeSession,"unconfirmed",default)).Single();
        Assert(unmatched.AlbumId=="a1"&&unmatched.Album=="Actual returned album"&&unmatched.RequestId=="track:unconfirmed", "unconfirmed context invented a navigation album or changed playback context");
        var artists=await api.GetArtistTracksAsync(FakeSession,"fixture",0,default);
        Assert(artists[0].AlbumId=="a1"&&artists[0].Album=="First"&&artists[0].RequestId=="track:a1"&&artists[1].AlbumId is null&&artists[1].RequestId=="no-album", "catalog first/missing album semantics changed");
        Assert(handler.Seen.Count==3&&!handler.Seen.Any(x=>x.Uri.AbsolutePath=="/search"), "catalog album was guessed by name");
    }

    private static async Task AlbumOfflineMetadata037()
    {
        var fixtureSuffix=Guid.NewGuid().ToString("N");
        var directory=Path.Combine(_directory,"album-metadata037-new-"+fixtureSuffix);
        var bufferDirectory=Path.Combine(_directory,"album-metadata037-buffer-"+fixtureSuffix);
        var wave=File.ReadAllBytes(WriteWave("album-metadata037-source.wav",1));
        var handler=new FakeHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(wave)}));
        var row=new TrackRow{Id="offline-track",RequestId="offline-track:playback-album",Title="Fixture",Artist="Artist",Album="Same title",AlbumId="confirmed-album",DurationMs=1000,Available=true};
        var playlist=new PlaylistDetails("fixture-owner","17","Fixture list","",3,[row]);
        OfflineItem item;
        using(var library=new OfflineLibrary(directory,new AudioBuffer(bufferDirectory,handler)))
        {
            item=await library.SaveAsync(row,new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/fixture.wav"),"mp3",192),default);
            await library.RememberPlaylistAsync(playlist,AudioQuality.Excellent,default);
            Assert(item.AlbumId==row.AlbumId&&library.Tracks().Single().AlbumId==row.AlbumId&&library.GetPlaylistTracks(playlist.Owner,playlist.Kind).Single().AlbumId==row.AlbumId, "AlbumId lost in new save/snapshot/TrackRow clone");
        }
        using(var fresh=new OfflineLibrary(directory))
        {
            Assert(fresh.Read().Single().AlbumId==row.AlbumId&&fresh.GetPlaylists().Single().Tracks.Single().AlbumId==row.AlbumId, "persisted AlbumId absent after independent readback");
            Assert(fresh.GetPlaylistStatus(playlist.Owner,playlist.Kind,[row]) is {Complete:true,ChangedSinceSnapshot:false,Downloaded:1}, "new persisted snapshot lost download completion");
        }
        var legacyDirectory=Path.Combine(_directory,"album-metadata037-legacy-"+fixtureSuffix);Directory.CreateDirectory(legacyDirectory);
        File.Copy(Path.Combine(directory,item.FileName),Path.Combine(legacyDirectory,item.FileName));
        var legacyItems=JsonNode.Parse(File.ReadAllText(Path.Combine(directory,"library.index.json")))!.AsArray();
        legacyItems[0]!.AsObject().Remove("AlbumId");legacyItems[0]!.AsObject().Remove("Artists");
        File.WriteAllText(Path.Combine(legacyDirectory,"library.index.json"),legacyItems.ToJsonString());
        var legacyPlaylist=JsonNode.Parse(File.ReadAllText(Path.Combine(directory,"playlists.index.json")))!;
        var legacyTrack=legacyPlaylist["playlists"]![0]!["tracks"]![0]!.AsObject();legacyTrack.Remove("albumId");legacyTrack.Remove("artists");
        File.WriteAllText(Path.Combine(legacyDirectory,"playlists.index.json"),legacyPlaylist.ToJsonString());
        var paths=new[]{"library.index.json","playlists.index.json"}.Select(name=>Path.Combine(legacyDirectory,name)).ToArray();
        var before=paths.Select(path=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        using(var legacy=new OfflineLibrary(legacyDirectory))
        {
            Assert(legacy.Read().Single().AlbumId is null&&legacy.Tracks().Single().AlbumId is null&&legacy.GetPlaylistTracks(playlist.Owner,playlist.Kind).Single().AlbumId is null, "old index inferred AlbumId from playback reference or name");
            Assert(legacy.GetPlaylistStatus(playlist.Owner,playlist.Kind,[row]) is {Complete:true,ChangedSinceSnapshot:false,Downloaded:1}, "navigation-only enrichment marked downloaded legacy playlist stale");
            var changed=new TrackRow{Id=row.Id,RequestId=row.RequestId,Title="Changed content",Artist=row.Artist,Album=row.Album,AlbumId=row.AlbumId,DurationMs=row.DurationMs,Available=true};
            Assert(legacy.GetPlaylistStatus(playlist.Owner,playlist.Kind,[changed]) is {Complete:false,ChangedSinceSnapshot:true}, "snapshot comparison stopped recognizing real metadata changes");
        }
        Assert(paths.Select(path=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).SequenceEqual(before), "reading/navigation enrichment rewrote legacy indexes");
        Assert(handler.Seen.Count==1, "synthetic metadata persistence made extra media requests");
    }
}
