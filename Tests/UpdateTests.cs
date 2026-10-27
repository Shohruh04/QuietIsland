using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace QuietIsland.Tests;

[TestClass]
public sealed class UpdateTests
{
    private string _directory = null!;
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("MZ-test-installer-never-executed");
    [TestInitialize]
    public void Setup() { _directory = Path.Combine(Path.GetTempPath(), "QuietIsland.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_directory); }
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, true);
    private static byte[] Metadata(string tag = "v2.0.0", bool draft = false, bool prerelease = false, string? digest = "valid", long? size = null, string? name = null, bool duplicate = false)
    {
        var asset = new { id = 123, name = name ?? "QuietIsland-Setup-2.0.0-win-x64.exe", state = "uploaded", size = size ?? Payload.Length,
            digest = digest == "valid" ? "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Payload)) : digest, browser_download_url = "https://evil.invalid/installer.exe" };
        return JsonSerializer.SerializeToUtf8Bytes(new { tag_name = tag, draft, prerelease, html_url = "https://evil.invalid/release", assets = duplicate ? new[] { asset, asset } : new[] { asset } });
    }
    private static UpdateRelease Release(byte[]? json = null) => GitHubUpdateService.ParseRelease(json ?? Metadata(), new Version(1, 0, 0))!;
    private static HttpResponseMessage JsonResponse(byte[]? json = null) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(json ?? Metadata()) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Interlocked.Increment(ref Count); return send(request, token); }
    }
    [TestMethod]
    [DataRow("v1.0.0")]
    [DataRow("v0.9.99")]
    [DataRow("v2.0.0-beta.1")]
    [DataRow("v2.0")]
    [DataRow("not-a-version")]
    public void RejectsOldEqualAndNonStableTags(string tag) => Assert.IsNull(GitHubUpdateService.ParseRelease(Metadata(tag), new Version(1, 0, 0)));
    [TestMethod]
    public void VersionComparisonUsesNumbersAndIgnoresAssemblyRevision()
    {
        Assert.IsNotNull(GitHubUpdateService.ParseRelease(Metadata("v1.10.0"), new Version(1, 9, 0, 0)));
        Assert.IsNull(GitHubUpdateService.ParseRelease(Metadata("v1.9.0"), new Version(1, 9, 0, 0)));
        Assert.IsNull(GitHubUpdateService.ParseRelease(Metadata(draft: true), new Version(1, 0, 0)));
        Assert.IsNull(GitHubUpdateService.ParseRelease(Metadata(prerelease: true), new Version(1, 0, 0)));
    }
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("sha256:bad")]
    [DataRow("sha1:123")]
    public void MissingOrInvalidHashAllowsOnlyManualDownload(string? digest) => Assert.IsNull(Release(Metadata(digest: digest)).Installer);
    [TestMethod]
    public void RequiresExactUnambiguousInstallerAndBoundedSize()
    {
        Assert.IsNull(Release(Metadata(name: "Other.exe")).Installer);
        Assert.IsNull(Release(Metadata(size: 0)).Installer);
        Assert.IsNull(Release(Metadata(size: 256L * 1024 * 1024 + 1)).Installer);
        Assert.IsNull(Release(Metadata(duplicate: true)).Installer);
        Assert.AreEqual(123L, Release().Installer!.Id);
        Assert.AreEqual(GitHubUpdateService.Repository + "/releases/tag/v2.0.0", Release().Page.AbsoluteUri);
    }
    [TestMethod]
    [DataRow("https://api.github.com/repos/Shohruh04/QuietIsland/releases/latest", false, true)]
    [DataRow("https://release-assets.githubusercontent.com/test?token=signed", true, true)]
    [DataRow("https://objects.githubusercontent.com/test", true, true)]
    [DataRow("https://release-assets.githubusercontent.com/test", false, false)]
    [DataRow("http://api.github.com/test", true, false)]
    [DataRow("https://api.github.com.evil.invalid/test", true, false)]
    [DataRow("https://evil.invalid/test", true, false)]
    [DataRow("https://api.github.com:444/test", true, false)]
    [DataRow("https://user@api.github.com/test", true, false)]
    public void PinsHttpsHostsAndPorts(string address, bool binary, bool expected) => Assert.AreEqual(expected, GitHubUpdateService.IsAllowedAddress(new Uri(address), binary));
    [TestMethod]
    public async Task DownloadsOnlyPinnedAssetAndVerifiesBeforeRenaming()
    {
        var assetRequests = 0;
        using var service = new GitHubUpdateService(new Handler((request, _) =>
        {
            Assert.IsTrue(request.Headers.UserAgent.ToString().StartsWith("QuietIsland/", StringComparison.Ordinal));
            if (request.RequestUri!.AbsolutePath.EndsWith("/latest")) return Task.FromResult(JsonResponse());
            Assert.AreEqual(GitHubUpdateService.Api + "/releases/assets/123", request.RequestUri.AbsoluteUri);
            Assert.AreEqual("application/octet-stream", request.Headers.Accept.Single().MediaType);
            assetRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        }));
        var path = await service.DownloadAsync(Release(), _directory, null, CancellationToken.None);
        CollectionAssert.AreEqual(Payload, File.ReadAllBytes(path));
        Assert.AreEqual(1, assetRequests);
        Assert.IsTrue(path.EndsWith(".exe", StringComparison.Ordinal));
        Assert.AreEqual(1, Directory.GetFiles(_directory).Length);
    }
    [TestMethod]
    [DataRow("truncated")]
    [DataRow("oversized")]
    [DataRow("corrupt")]
    public async Task RejectsIncompleteExtraOrCorruptBytesAndRemovesPartial(string kind)
    {
        var bytes = kind == "truncated" ? Payload[..^1] : kind == "oversized" ? Payload.Concat(new byte[] { 0 }).ToArray() : new byte[Payload.Length];
        using var service = new GitHubUpdateService(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/latest")) return Task.FromResult(JsonResponse());
            var content = new ByteArrayContent(bytes); content.Headers.ContentLength = Payload.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.DownloadAsync(Release(), _directory, null, CancellationToken.None));
        Assert.AreEqual(0, Directory.GetFiles(_directory).Length);
    }
    [TestMethod]
    public async Task RejectsChangedReleaseBeforeDownloadingAnyExecutable()
    {
        var handler = new Handler((_, _) => Task.FromResult(JsonResponse(Metadata("v2.0.1"))));
        using var service = new GitHubUpdateService(handler);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.DownloadAsync(Release(), _directory, null, CancellationToken.None));
        Assert.AreEqual(1, handler.Count);
        Assert.AreEqual(0, Directory.GetFiles(_directory).Length);
    }
    [TestMethod]
    public async Task RejectsUntrustedRedirectBeforeRequestingIt()
    {
        var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/latest")) return Task.FromResult(JsonResponse());
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.invalid/malware.exe"); return Task.FromResult(response);
        });
        using var service = new GitHubUpdateService(handler);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.DownloadAsync(Release(), _directory, null, CancellationToken.None));
        Assert.AreEqual(2, handler.Count);
    }
    [TestMethod]
    public async Task AcceptsPinnedCdnRedirectAndStopsRedirectLoops()
    {
        var redirect = new Uri("https://release-assets.githubusercontent.com/file");
        using (var service = new GitHubUpdateService(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/latest")) return Task.FromResult(JsonResponse());
            if (request.RequestUri.Host == redirect.Host) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = redirect } });
        }))) Assert.IsTrue(File.Exists(await service.DownloadAsync(Release(), _directory, null, CancellationToken.None)));
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri(GitHubUpdateService.Api + "/loop") } }));
        using var looping = new GitHubUpdateService(handler);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => looping.CheckAsync(new Version(1, 0, 0), CancellationToken.None));
        Assert.AreEqual(5, handler.Count);
    }
    [TestMethod]
    public async Task OfflineAndRateLimitedChecksFailWithoutReportingUpToDate()
    {
        using var offline = new GitHubUpdateService(new Handler((_, _) => throw new HttpRequestException("Offline")));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => offline.CheckAsync(new Version(1, 0, 0), CancellationToken.None));
        using var limited = new GitHubUpdateService(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => limited.CheckAsync(new Version(1, 0, 0), CancellationToken.None));
    }
    [TestMethod]
    public async Task ConcurrentAutomaticAndManualChecksShareOneRequest()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((_, token) => response.Task.WaitAsync(token));
        using var controller = new UpdateController(new GitHubUpdateService(handler), new UpdateInstaller(), new AppSettings(), new StartupRegistration(), () => { }, _directory);
        var automatic = controller.CheckSharedAsync(); var manual = controller.CheckSharedAsync();
        Assert.AreEqual(1, handler.Count);
        response.SetResult(JsonResponse());
        Assert.AreEqual(await automatic, await manual);
        Assert.IsFalse(controller.IsChecking);
    }
    [TestMethod]
    public async Task ShutdownCancelsOutstandingChecks()
    {
        var handler = new Handler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return JsonResponse(); });
        var controller = new UpdateController(new GitHubUpdateService(handler), new UpdateInstaller(), new AppSettings(), new StartupRegistration(), () => { }, _directory);
        var pending = controller.CheckSharedAsync(); controller.Dispose();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
    }
    [TestMethod]
    public void InstallerPreservesActualStartupPreferenceAndUsesStructuredPaths()
    {
        var path = Path.Combine(_directory, "installer with spaces.exe");
        var install = Path.Combine(_directory, "custom install");
        var enabled = UpdateInstaller.CreateStartInfo(path, install, true);
        CollectionAssert.Contains(enabled.ArgumentList.ToArray(), "/MERGETASKS=startup");
        CollectionAssert.Contains(enabled.ArgumentList.ToArray(), "/DIR=" + install);
        var disabled = UpdateInstaller.CreateStartInfo(path, install, false);
        CollectionAssert.Contains(disabled.ArgumentList.ToArray(), "/MERGETASKS=!startup");
        CollectionAssert.Contains(disabled.ArgumentList.ToArray(), "/NORESTART");
        Assert.AreEqual(_directory, disabled.WorkingDirectory);
        Assert.IsFalse(disabled.ArgumentList.Any(argument => argument.Contains("SILENT", StringComparison.Ordinal)));
    }
    [TestMethod]
    public void PortablePathsDoNotMatchRegisteredInstallation()
    {
        Assert.IsTrue(UpdateInstaller.IsSameDirectory(@"C:\Apps\QuietIsland\", @"c:\apps\QUIETISLAND"));
        Assert.IsFalse(UpdateInstaller.IsSameDirectory(@"D:\Portable\QuietIsland", @"C:\Apps\QuietIsland"));
        Assert.IsFalse(UpdateInstaller.IsSameDirectory(@"C:\Apps\QuietIsland-copy", @"C:\Apps\QuietIsland"));
        Assert.IsFalse(UpdateInstaller.IsSameDirectory(@"C:\Apps\QuietIsland", "\0invalid"));
    }
    [TestMethod]
    public void DailyScheduleSurvivesRestartClockChangesAndCanBeDisabled()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.IsFalse(UpdateController.IsCheckDue(now.AddHours(-23), now));
        Assert.IsTrue(UpdateController.IsCheckDue(now.AddHours(-24), now));
        Assert.IsTrue(UpdateController.IsCheckDue(now.AddDays(1), now));
        var settings = new AppSettings(); var saves = 0;
        using var controller = new UpdateController(new GitHubUpdateService(new Handler((_, _) => throw new InvalidOperationException("Must not send"))), new UpdateInstaller(), settings, new StartupRegistration(), () => saves++, _directory);
        controller.ToggleAutomatic(); Assert.IsFalse(settings.AutoCheckUpdates); Assert.AreEqual(1, saves);
    }
    [TestMethod]
    public async Task AutomaticChecksOnlyNotifyAndDoNotDownloadOrRepeatAfterRestart()
    {
        var handler = new Handler((request, _) => { Assert.IsTrue(request.RequestUri!.AbsolutePath.EndsWith("/latest")); return Task.FromResult(JsonResponse()); });
        using (var controller = new UpdateController(new GitHubUpdateService(handler), new UpdateInstaller(), new AppSettings(), new StartupRegistration(), () => { }, _directory))
        {
            var notices = 0; controller.NotificationRequested += _ => notices++;
            await controller.CheckAutomaticallyAsync(); await controller.CheckAutomaticallyAsync();
            Assert.AreEqual(1, notices); Assert.AreEqual(1, handler.Count);
            Assert.AreEqual(new Version(2, 0, 0), controller.AvailableVersion);
            Assert.AreEqual(0, Directory.GetFiles(_directory, "*.exe*").Length);
        }
        var restarted = new Handler((_, _) => throw new InvalidOperationException("Daily throttle must survive restart"));
        using var afterRestart = new UpdateController(new GitHubUpdateService(restarted), new UpdateInstaller(), new AppSettings(), new StartupRegistration(), () => { }, _directory);
        await afterRestart.CheckAutomaticallyAsync(); Assert.AreEqual(0, restarted.Count);
    }
    [TestMethod]
    public async Task DisabledAutomaticChecksSendNoRequests()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Disabled"));
        using var controller = new UpdateController(new GitHubUpdateService(handler), new UpdateInstaller(), new AppSettings { AutoCheckUpdates = false }, new StartupRegistration(), () => { }, _directory);
        await controller.CheckAutomaticallyAsync(); Assert.AreEqual(0, handler.Count);
    }
    [TestMethod]
    public async Task DisablingChecksDuringRequestSuppressesNotification()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((_, token) => response.Task.WaitAsync(token));
        using var controller = new UpdateController(new GitHubUpdateService(handler), new UpdateInstaller(), new AppSettings(), new StartupRegistration(), () => { }, _directory);
        var pending = controller.CheckAutomaticallyAsync(); var notices = 0;
        controller.NotificationRequested += _ => notices++; controller.ToggleAutomatic();
        response.SetResult(JsonResponse()); await pending;
        Assert.AreEqual(0, notices);
    }
    [TestMethod]
    public async Task ServiceTimeoutIsNotTreatedAsUserCancellation()
    {
        using var service = new GitHubUpdateService(new Handler((_, _) => throw new TaskCanceledException("Simulated service timeout")));
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => service.CheckAsync(new Version(1, 0, 0), CancellationToken.None));
    }
    private sealed class PausedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource<bool> FirstChunk { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_read) { await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
            _read = true;
            var count = await base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], token);
            FirstChunk.TrySetResult(true); return count;
        }
    }
    [TestMethod]
    public async Task CancellingDuringDownloadLeavesNoPartialOrExecutable()
    {
        using var stream = new PausedStream(Payload);
        using var service = new GitHubUpdateService(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/latest")) return Task.FromResult(JsonResponse());
            var content = new StreamContent(stream); content.Headers.ContentLength = Payload.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        using var cancellation = new CancellationTokenSource();
        var pending = service.DownloadAsync(Release(), _directory, null, cancellation.Token);
        await stream.FirstChunk.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        Assert.AreEqual(0, Directory.GetFiles(_directory).Length);
    }
    [TestMethod]
    public async Task DownloadTimeoutIsReportedAndRemovesPartial()
    {
        using var service = new GitHubUpdateService(new Handler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/latest") ? Task.FromResult(JsonResponse()) : throw new TaskCanceledException("Simulated download timeout")));
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => service.DownloadAsync(Release(), _directory, null, CancellationToken.None));
        Assert.AreEqual(0, Directory.GetFiles(_directory).Length);
    }
    [TestMethod]
    public void CleanupOnlyRemovesOldUpdaterOwnedFiles()
    {
        var old = Path.Combine(_directory, "QuietIsland-2.0.0-" + Guid.NewGuid().ToString("N") + ".exe");
        var recent = Path.Combine(_directory, "QuietIsland-2.0.1-" + Guid.NewGuid().ToString("N") + ".exe.partial");
        var unrelated = Path.Combine(_directory, "QuietIsland-my-backup.exe");
        File.WriteAllText(old, "old"); File.WriteAllText(recent, "recent"); File.WriteAllText(unrelated, "keep");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8)); File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-8));
        GitHubUpdateService.CleanOldDownloads(_directory, DateTime.UtcNow);
        Assert.IsFalse(File.Exists(old)); Assert.IsTrue(File.Exists(recent)); Assert.IsTrue(File.Exists(unrelated));
    }
    [TestMethod]
    [TestCategory("LiveGitHub")]
    public async Task OptInLiveReleaseDownloadVerifiesWithoutExecuting()
    {
        if (Environment.GetEnvironmentVariable("QUIETISLAND_TEST_LIVE_GITHUB") != "1") Assert.Inconclusive("Set QUIETISLAND_TEST_LIVE_GITHUB=1 to download and verify the current public installer without executing it.");
        using var service = new GitHubUpdateService();
        var release = await service.CheckAsync(new Version(0, 0, 0), CancellationToken.None);
        Assert.IsNotNull(release); Assert.IsNotNull(release.Installer);
        var path = await service.DownloadAsync(release, _directory, null, CancellationToken.None);
        using var file = File.OpenRead(path);
        Assert.AreEqual(release.Installer.Size, file.Length);
        Assert.AreEqual(release.Installer.Sha256, Convert.ToHexStringLower(SHA256.HashData(file)));
        Console.WriteLine($"Live GitHub release {release.Tag}: verified {file.Length} bytes; installer was not executed.");
    }
}
