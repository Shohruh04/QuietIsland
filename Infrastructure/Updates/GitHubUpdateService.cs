using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QuietIsland;

internal sealed record UpdateAsset(long Id, string Name, long Size, string Sha256);
internal sealed record UpdateRelease(Version Version, string Tag, Uri Page, UpdateAsset? Installer);

/// <summary>Reads public releases and downloads only a pinned, size/hash-verified installer.</summary>
internal sealed class GitHubUpdateService : IDisposable
{
    internal const string Repository = "https://github.com/Shohruh04/QuietIsland";
    internal const string Api = "https://api.github.com/repos/Shohruh04/QuietIsland";
    private const long MaximumInstallerBytes = 256 * 1024 * 1024;
    private readonly HttpClient _client;

    public GitHubUpdateService(HttpMessageHandler? handler = null)
    {
        _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("QuietIsland/" + CurrentVersion.ToString(3));
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }
    internal static Version CurrentVersion
    {
        get { var version = typeof(App).Assembly.GetName().Version!; return new(version.Major, version.Minor, version.Build); }
    }
    public async Task<UpdateRelease?> CheckAsync(Version current, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await GetAsync(new Uri(Api + "/releases/latest"), false, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await ReadBoundedAsync(response, 1024 * 1024, timeout.Token).ConfigureAwait(false);
            return ParseRelease(bytes, current);
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested) { throw new TimeoutException("GitHub update check timed out.", exception); }
    }
    internal static UpdateRelease? ParseRelease(ReadOnlyMemory<byte> json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(tag[1..], out var version) || version <= new Version(current.Major, current.Minor, Math.Max(0, current.Build))) return null;
        var name = $"QuietIsland-Setup-{version.ToString(3)}-win-x64.exe";
        UpdateAsset? installer = null;
        var matches = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            matches++;
            if (asset.GetProperty("state").GetString() != "uploaded" || !asset.TryGetProperty("digest", out var digestValue)) continue;
            var digest = digestValue.ValueKind == JsonValueKind.String ? digestValue.GetString() : null;
            var size = asset.GetProperty("size").GetInt64();
            var id = asset.GetProperty("id").GetInt64();
            if (id <= 0 || size <= 0 || size > MaximumInstallerBytes || digest is null ||
                !Regex.IsMatch(digest, @"^sha256:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)) continue;
            installer = new(id, name, size, digest[7..].ToLowerInvariant());
        }
        // Without an unambiguous verified asset, the UI offers the release page instead.
        return new(version, tag, new Uri(Repository + "/releases/tag/" + tag), matches == 1 ? installer : null);
    }
    public async Task<string> DownloadAsync(UpdateRelease release, string directory, IProgress<int>? progress, CancellationToken token)
    {
        var asset = release.Installer ?? throw new InvalidOperationException("No verified installer is available.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        // Recheck the candidate after the confirmation dialog; never substitute a different release.
        var latest = await CheckAsync(new Version(0, 0, 0), timeout.Token).ConfigureAwait(false);
        if (latest != release) throw new InvalidDataException("The release changed. Check for updates again.");
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, "QuietIsland-" + release.Version.ToString(3) + "-" + Guid.NewGuid().ToString("N") + ".exe");
        var partialPath = finalPath + ".partial";
        try
        {
            using var response = await GetAsync(new Uri(Api + "/releases/assets/" + asset.Id), true, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != asset.Size) throw new InvalidDataException("Installer size mismatch.");
            using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read, percent = -1;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    total += read;
                    if (total > asset.Size) throw new InvalidDataException("Installer is larger than expected.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    var nextPercent = (int)(total * 100 / asset.Size);
                    if (nextPercent != percent) { percent = nextPercent; progress?.Report(percent); }
                }
                if (total != asset.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(asset.Sha256)))
                    throw new InvalidDataException("Installer verification failed.");
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(partialPath, finalPath);
            return finalPath;
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested) { throw new TimeoutException("Update download timed out.", exception); }
        finally { if (File.Exists(partialPath)) File.Delete(partialPath); }
    }
    private async Task<HttpResponseMessage> GetAsync(Uri address, bool binary, CancellationToken token)
    {
        for (var redirects = 0; ; redirects++)
        {
            if (!IsAllowedAddress(address, binary)) throw new InvalidDataException("Unexpected update download host.");
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.ParseAdd(binary ? "application/octet-stream" : "application/vnd.github+json");
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (redirects >= 4 || location is null) throw new InvalidDataException("Invalid update redirect.");
            address = location.IsAbsoluteUri ? location : new Uri(address, location);
        }
    }
    internal static bool IsAllowedAddress(Uri address, bool binary) => address.Scheme == Uri.UriSchemeHttps && address.Port == 443 &&
        address.UserInfo.Length == 0 && address.Fragment.Length == 0 &&
        (address.Host == "api.github.com" || (binary && address.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com"));
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Release response too large.");
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("Release response too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    public void Dispose() => _client.Dispose();
    internal static void CleanOldDownloads(string directory, DateTime utcNow)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "QuietIsland-*"))
        {
            // Only this updater's generated names; never delete arbitrary user files/subdirectories.
            if (!Regex.IsMatch(Path.GetFileName(file), @"^QuietIsland-\d+\.\d+\.\d+-[a-f0-9]{32}\.exe(\.partial)?$", RegexOptions.CultureInvariant)) continue;
            try { if (File.GetLastWriteTimeUtc(file) < utcNow.AddDays(-7)) File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
