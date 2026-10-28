using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckVersioning040()
    {
        await Run("Versioning040: runtime footer and update state retain the preview identity", V040Metadata);
        await Run("Versioning040: portable stable feed promotes preview to final and prevents equal or older offers", V040Portable);
        await Run("Versioning040: Velopack stable GitHub feed promotes preview to final without preview assets or downloads", V040InstalledFeed);
    }

    private static Task V040Metadata()
    {
        var assembly = typeof(UpdateSupport).Assembly;
        const string expected = "0.5.2";
        Assert(assembly.GetName().Version == new Version(0, 5, 2, 0), "numeric assembly identity drifted");
        Assert(assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version == "0.5.2.0", "numeric file identity drifted");
        Assert(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion == expected,
            "full preview identity was lost in informational metadata");
        Assert(FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion == expected, "Windows product metadata claims a stable version");
        Assert(AppBranding.CurrentVersion == expected && AppBranding.VersionLabel == "v" + expected, "footer identity lost the preview suffix");
        var service = new UpdateSupport(Path.Combine(_directory, "versioning040-missing-feed.json"), false,
            _ => throw new InvalidOperationException("metadata check created an update backend"));
        Assert(service.State.CurrentVersion == expected && service.State.Status == AppUpdateStatus.NotConfigured,
            "update settings identity disagrees with the footer or missing feed created a check");
        Assert(SemanticVersion.Parse("0.4.0-preview.10") > SemanticVersion.Parse("0.4.0-preview.2")
            && SemanticVersion.Parse("0.4.0") > SemanticVersion.Parse("0.4.0-preview.10")
            && SemanticVersion.Parse("0.4.1") > SemanticVersion.Parse("0.4.0"), "preview or patch ordering is not semantic");
        return Task.CompletedTask;
    }

    private static async Task V040Portable()
    {
        async Task<string?> Check(string current, string target, bool prerelease = false)
            => await UpdateSupport.CheckPortableReleaseAsync(current, CancellationToken.None,
                new FakeHandler((request, _) =>
                {
                    Assert(request.RequestUri?.AbsoluteUri == "https://api.github.com/repos/senduora/yamuse/releases/latest"
                        && request.Method == HttpMethod.Get && request.Headers.Authorization is null,
                        "portable version comparison changed its metadata authority");
                    return Task.FromResult(Json($$"""{"tag_name":"{{target}}","draft":false,"prerelease":{{prerelease.ToString().ToLowerInvariant()}}}"""));
                }));
        Assert(await Check("0.4.0-preview.1", "v0.4.0") == "0.4.0", "final with the same numeric version was not offered to preview");
        Assert(await Check("0.4.0", "v0.4.0") is null, "equal stable version was offered again");
        Assert(await Check("0.4.0-preview.1", "v0.3.16") is null, "last published older line was offered as a downgrade");
        Assert(await Check("0.4.1", "v0.4.0") is null, "older patch was offered as a downgrade");
        Assert(await Check("0.4.0-preview.1", "v0.4.1") == "0.4.1", "newer stable patch was not offered");
        Assert(await Check("0.4.0", "v0.4.0+release.7") is null, "build metadata created an update with equal precedence");
        await Throws<InvalidDataException>(() => Check("0.4.0-preview.1", "v0.5.0-preview.1", true));
        await Throws<InvalidDataException>(() => Check("0.4.0-preview.1", "v0.5.0-preview.1"));
    }

    private static async Task V040InstalledFeed()
    {
        foreach (var (current, expected) in new[]
        {
            ("0.3.16", "0.4.0"), ("0.4.0-preview.1", "0.4.0"), ("0.4.0", (string?)null), ("0.4.1", (string?)null)
        })
        {
            var directory = Path.Combine(_directory, "versioning040-feed", current);
            Directory.CreateDirectory(directory);
            var downloader = new V040GithubDownloader();
            var source = new GithubSource("https://github.com/senduora/yamuse", null, false, downloader);
            var locator = new TestVelopackLocator("YMusic.Native", current, directory);
            var manager = new UpdateManager(source, locator: locator);
            var update = await manager.CheckForUpdatesAsync();
            Assert(manager.CurrentVersion?.ToString() == current && update?.TargetFullRelease.Version.ToString() == expected,
                "installed stable feed version comparison drifted for " + current);
            Assert(downloader.Requests.Any(url => url == V040GithubDownloader.StableFeed)
                && downloader.Requests.All(url => url != V040GithubDownloader.PreviewFeed)
                && downloader.Downloads == 0, "stable check read preview assets or downloaded/applied a package");
        }
    }

    private sealed class V040GithubDownloader : IFileDownloader
    {
        internal const string StableFeed = "https://github.com/senduora/yamuse/releases/download/v0.4.0/releases.win.json";
        internal const string PreviewFeed = "https://github.com/senduora/yamuse/releases/download/v0.5.0-preview.1/releases.win.json";
        internal List<string> Requests { get; } = [];
        internal int Downloads { get; private set; }

        private string Read(string url, IDictionary<string, string>? headers)
        {
            Requests.Add(url);
            Assert(headers is null || !headers.Keys.Any(key => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)),
                "public fixture received credentials");
            if (url.StartsWith("https://api.github.com/repos/senduora/yamuse/releases", StringComparison.Ordinal))
                return $$"""
                    [
                      {"name":"0.5.0-preview.1","prerelease":true,"published_at":"2026-10-27T00:00:00Z","assets":[{"name":"releases.win.json","browser_download_url":"{{PreviewFeed}}","url":"https://api.github.com/assets/preview"}]},
                      {"name":"0.4.0","prerelease":false,"published_at":"2026-10-26T00:00:00Z","assets":[{"name":"releases.win.json","browser_download_url":"{{StableFeed}}","url":"https://api.github.com/assets/stable"}]}
                    ]
                    """;
            Assert(url == StableFeed, "SDK requested a nonstable or unexpected asset");
            return """{"Assets":[{"PackageId":"YMusic.Native","Version":"0.4.0","Type":"Full","FileName":"Yamuse-0.4.0-full.nupkg","SHA1":"0000000000000000000000000000000000000000","SHA256":"0000000000000000000000000000000000000000000000000000000000000000","Size":1}]}""";
        }

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
            => Task.FromResult(Read(url, headers));
        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
            => Task.FromResult(Encoding.UTF8.GetBytes(Read(url, headers)));
        public Task DownloadFile(string url, string targetFile, Action<int>? progress = null,
            IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancellationToken = default)
        {
            Downloads++;
            throw new InvalidOperationException("metadata-only fixture attempted a package download");
        }
    }
}
