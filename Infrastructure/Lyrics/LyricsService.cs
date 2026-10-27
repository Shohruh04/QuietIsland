using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QuietIsland;

internal sealed record LyricLine(double Seconds, string Text);
internal sealed record LyricsDocument(string Status, List<LyricLine> Lines, string? Plain = null);
internal sealed record LyricsCandidate(string Title, string Artist, double Duration, string Album = "", bool Synced = false);
internal sealed record CachedLyricsCandidate(LyricsCandidate Candidate, LyricsDocument Document);
internal sealed record LyricsCacheEntry(LyricsDocument? Selected, List<CachedLyricsCandidate> Options);

internal sealed class LyricsService : IDisposable
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1024 * 1024 };
    private readonly SemaphoreSlim _gate = new(1);
    private readonly SemaphoreSlim _cacheGate = new(1);
    private CancellationTokenSource? _pending;
    private string _key = "";
    private int _generation;
    private bool _enabled, _disposed;
    private DateTimeOffset _retryAfter;
    private List<CachedLyricsCandidate> _options = new();
    public LyricsDocument Document { get; private set; } = new("Включите музыку", new());
    public IReadOnlyList<LyricsCandidate> Candidates { get; private set; } = Array.Empty<LyricsCandidate>();
    public event Action? Changed;

    public LyricsService() => _client.DefaultRequestHeaders.UserAgent.ParseAdd("QuietIsland/1.0 (https://github.com/Shohruh04/QuietIsland)");

    public void Update(MediaState track, bool enabled)
    {
        if (_disposed) return;
        var key = track.Available ? track.Title + "\n" + track.Artist + "\n" + track.Album + "\n" + Math.Round(track.Duration) : "";
        if (_key == key && _enabled == enabled) return;
        _key = key; _enabled = enabled;
        BeginLoad(track, skipCache: false);
    }
    public void Retry(MediaState track, bool enabled)
    {
        if (_disposed) return;
        _key = track.Available ? track.Title + "\n" + track.Artist + "\n" + track.Album + "\n" + Math.Round(track.Duration) : "";
        _enabled = enabled;
        BeginLoad(track, skipCache: enabled);
    }
    private void BeginLoad(MediaState track, bool skipCache)
    {
        var version = ++_generation;
        CancelPending();
        _options = new(); Candidates = Array.Empty<LyricsCandidate>();
        if (!track.Available || _key.Length == 0)
        { Document = new("Включите музыку", new()); Changed?.Invoke(); return; }
        _pending = new CancellationTokenSource();
        _ = LoadAsync(track, _key, version, _enabled, skipCache, _pending.Token);
    }
    public async Task<bool> ImportAsync(string path)
    {
        if (_disposed) return false;
        if (_key.Length == 0) { Document = new("Сначала выберите песню", new()); Changed?.Invoke(); return false; }
        var key = _key;
        var version = ++_generation;
        CancelPending();
        _pending = new CancellationTokenSource();
        var token = _pending.Token;
        try
        {
            var lines = await Task.Run(async () =>
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
                return Parse(await LyricsBudget.ReadTextAsync(input, token).ConfigureAwait(false), token);
            }, token);
            if (!Current(key, version, token)) return false;
            if (lines.Count == 0) { Document = new("В файле нет временных меток", new()); Changed?.Invoke(); return false; }
            Document = new("Локальный синхронный текст", lines);
            _options = new(); Candidates = Array.Empty<LyricsCandidate>();
            Changed?.Invoke();
            _ = SaveCacheAsync(key, version, new(Document, _options));
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            AppLog.Write(new InvalidOperationException("Ошибка импорта текста: " + ex.GetType().Name));
            Publish(key, version, new("Не удалось загрузить текст песни", new()), token);
            return false;
        }
    }
    public void SelectCandidate(int index)
    {
        if (_disposed || _key.Length == 0 || index < 0 || index >= _options.Count) return;
        var version = ++_generation; CancelPending();
        var selected = _options[index];
        Document = selected.Document with { Status = selected.Candidate.Synced ? "Выбранная версия · LRCLIB" : selected.Document.Status };
        Changed?.Invoke();
        _ = SaveCacheAsync(_key, version, new(Document, _options));
    }

    private async Task LoadAsync(MediaState track, string key, int version, bool online, bool skipCache, CancellationToken token)
    {
        Publish(key, version, new(online ? "Поиск текста песни…" : "Проверка локального текста…", new()), token);
        try
        {
            if (!skipCache)
            {
                var cached = await ReadCacheAsync(key, token);
                if (cached != null && Current(key, version, token))
                {
                    SetOptions(cached.Options);
                    Document = cached.Selected ?? SelectionPrompt(cached.Options.Count);
                    Changed?.Invoke(); return;
                }
            }
            if (!online) { Publish(key, version, new("Онлайн-текст отключён", new()), token); return; }
            await Task.Delay(500, token);
            await _gate.WaitAsync(token);
            try
            {
                if (!await WaitForSlotAsync(token)) { Publish(key, version, Busy(), token); return; }
                var query = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(track.Title) + "&artist_name=" + Uri.EscapeDataString(PrimaryArtist(track.Artist));
                if (!string.IsNullOrWhiteSpace(track.Album)) query += "&album_name=" + Uri.EscapeDataString(track.Album);
                if (track.Duration is >= 1 and <= 3600) query += "&duration=" + Math.Round(track.Duration).ToString(CultureInfo.InvariantCulture);
                using var response = await _client.GetAsync(query, token);
                RememberBackoff(response);
                if (response.StatusCode == HttpStatusCode.TooManyRequests) { Publish(key, version, Busy(), token); return; }
                var options = new List<CachedLyricsCandidate>();
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable)
                {
                    // /get can trigger a slow provider lookup. Search existing records instead,
                    // but only after the exact endpoint's Retry-After has elapsed.
                    if (!await WaitForSlotAsync(token)) { Publish(key, version, Busy(), token); return; }
                    var searchUrl = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(track.Title) + "&artist_name=" + Uri.EscapeDataString(PrimaryArtist(track.Artist));
                    using var search = await _client.GetAsync(searchUrl, token);
                    RememberBackoff(search);
                    if (search.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                    { Publish(key, version, Busy(), token); return; }
                    search.EnsureSuccessStatusCode();
                    options = await ReadOptionsAsync(search.Content, true, track, token);
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    options = await ReadOptionsAsync(response.Content, false, track, token);
                }
                if (!Current(key, version, token)) return;
                SetOptions(options);
                var automatic = options.FirstOrDefault(o => o.Candidate.Synced && IsAutomaticMatch(o.Candidate, track));
                Document = automatic?.Document ?? SelectionPrompt(options.Count);
                Changed?.Invoke();
                if (options.Count > 0) await SaveCacheAsync(key, version, new(automatic?.Document, options));
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested) Publish(key, version, new("Сервис текста не ответил", new()), token); }
        catch (Exception ex)
        {
            // Do not log HTTP bodies, lyrics, or request URLs containing music metadata.
            AppLog.Write(new InvalidOperationException("Ошибка получения текста: " + ex.GetType().Name));
            Publish(key, version, new("Не удалось загрузить текст песни", new()), token);
        }
    }
    private static LyricsDocument Busy() => new("Сервис текста занят. Повторите позже.", new());
    private static LyricsDocument SelectionPrompt(int count) => new(count == 0 ? "Текст этой песни не найден" : "Выберите версию текста в меню", new());
    private void SetOptions(List<CachedLyricsCandidate> options)
    { _options = options; Candidates = options.Select(o => o.Candidate).ToArray(); }
    private void RememberBackoff(HttpResponseMessage response)
    {
        var minimum = DateTimeOffset.UtcNow.AddMilliseconds(500);
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            var fallback = response.StatusCode == HttpStatusCode.TooManyRequests ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(1);
            var requested = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? fallback);
            if (requested > minimum) minimum = requested;
        }
        if (minimum > _retryAfter) _retryAfter = minimum;
    }
    private async Task<bool> WaitForSlotAsync(CancellationToken token)
    {
        var wait = _retryAfter - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.FromSeconds(5)) return false;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
        return true;
    }
    private static Task<List<CachedLyricsCandidate>> ReadOptionsAsync(HttpContent content, bool search, MediaState track, CancellationToken token) => Task.Run(async () =>
    {
        using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(await LyricsBudget.ReadTextAsync(input, token).ConfigureAwait(false));
        var options = new List<CachedLyricsCandidate>();
        var items = search && json.RootElement.ValueKind == JsonValueKind.Array
            ? json.RootElement.EnumerateArray().Take(20) : new[] { json.RootElement };
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            try { var parsed = ReadCandidate(item, token); if (parsed != null) options.Add(parsed); }
            catch (InvalidDataException) { } // An oversized version cannot make all other versions unusable.
        }
        return options.OrderByDescending(o => IsAutomaticMatch(o.Candidate, track))
            .ThenBy(o => track.Duration > 0 ? Math.Abs(o.Candidate.Duration - track.Duration) : 0).ToList();
    }, token);
    private static CachedLyricsCandidate? ReadCandidate(JsonElement root, CancellationToken token)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        var title = Text(root, "trackName"); if (title.Length == 0) title = Text(root, "name");
        var artist = Text(root, "artistName");
        var duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var seconds) && double.IsFinite(seconds) ? seconds : 0;
        var instrumental = root.TryGetProperty("instrumental", out var ins) && ins.ValueKind == JsonValueKind.True;
        var lines = Parse(Text(root, "syncedLyrics"), token); var plain = Text(root, "plainLyrics");
        if (!instrumental && lines.Count == 0 && string.IsNullOrWhiteSpace(plain)) return null;
        var status = instrumental ? "Инструментальная композиция" : lines.Count > 0 ? "Синхронный текст · LRCLIB" : "Текст без временных меток";
        var candidate = new CachedLyricsCandidate(new(title, artist, Math.Max(0, duration), Text(root, "albumName"), lines.Count > 0), new(status, lines, plain));
        return LyricsBudget.ValidCache(new(null, new() { candidate })) ? candidate : null;
    }
    internal static string PrimaryArtist(string artist)
    {
        var value = Regex.Replace(artist.Trim(), @"^by\s+", "", RegexOptions.IgnoreCase);
        var comma = value.IndexOf(',');
        return (comma >= 0 ? value[..comma] : value).Trim();
    }
    internal static string NormalizeMatch(string value)
    {
        var result = new StringBuilder();
        foreach (var c in value.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)) result.Append(char.ToLowerInvariant(c));
        return result.ToString();
    }
    internal static bool IsAutomaticMatch(LyricsCandidate candidate, MediaState track) =>
        track.Duration > 0 && candidate.Duration > 0 && Math.Abs(candidate.Duration - track.Duration) <= 3 &&
        NormalizeMatch(track.Title).Length > 0 && NormalizeMatch(PrimaryArtist(track.Artist)).Length > 0 &&
        NormalizeMatch(candidate.Title) == NormalizeMatch(track.Title) &&
        NormalizeMatch(PrimaryArtist(candidate.Artist)) == NormalizeMatch(PrimaryArtist(track.Artist));

    private static string CacheDirectory => Path.Combine(AppPaths.DataDirectory, "Lyrics");
    private static string CachePath(string key) => Path.Combine(CacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
    private static Task<LyricsCacheEntry?> ReadCacheAsync(string key, CancellationToken token) => Task.Run(async () =>
    {
        var path = CachePath(key);
        try
        {
            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= TimeSpan.FromDays(30)) return null;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
            var data = await LyricsBudget.ReadTextAsync(input, token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(data);
            LyricsCacheEntry? entry;
            if (json.RootElement.TryGetProperty("Lines", out _))
            {
                var old = JsonSerializer.Deserialize<LyricsDocument>(data);
                entry = old == null ? null : new(old, new());
            }
            else entry = JsonSerializer.Deserialize<LyricsCacheEntry>(data);
            return entry != null && LyricsBudget.ValidCache(entry) ? entry : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }, token);
    private async Task SaveCacheAsync(string key, int version, LyricsCacheEntry entry)
    {
        await _cacheGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(async () =>
            {
                string? temporary = null;
                try
                {
                    if (!Current(key, version, default)) return;
                    Directory.CreateDirectory(CacheDirectory);
                    var path = CachePath(key);
                    temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
                        await LyricsBudget.WriteCacheAsync(output, entry).ConfigureAwait(false);
                    if (!Current(key, version, default)) return;
                    File.Move(temporary, path, true); temporary = null;
                    var files = new DirectoryInfo(CacheDirectory).GetFiles("*.json")
                        .Where(f => Regex.IsMatch(f.Name, @"^[A-F0-9]{64}\.json$"))
                        .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
                    for (var i = 0; i < files.Length; i++)
                        if (i >= 100 || DateTime.UtcNow - files[i].LastWriteTimeUtc >= TimeSpan.FromDays(30)) files[i].Delete();
                }
                catch (Exception ex) { AppLog.Write(new IOException("Ошибка кеша текста: " + ex.GetType().Name)); }
                finally { if (temporary != null) { try { File.Delete(temporary); } catch { } } }
            }).ConfigureAwait(false);
        }
        finally { _cacheGate.Release(); }
    }
    private bool Current(string key, int version, CancellationToken token) => !Volatile.Read(ref _disposed) && key == Volatile.Read(ref _key) && version == Volatile.Read(ref _generation) && !token.IsCancellationRequested;
    private void Publish(string key, int version, LyricsDocument document, CancellationToken token)
    { if (Current(key, version, token)) { Document = document; Changed?.Invoke(); } }
    private void CancelPending() { _pending?.Cancel(); _pending?.Dispose(); _pending = null; }

    public (string previous, string current, string next) At(double seconds)
    {
        var lines = Document.Lines;
        if (lines.Count == 0)
        {
            var plain = Document.Plain?.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return ("", plain?.FirstOrDefault() ?? Document.Status, plain?.Skip(1).FirstOrDefault() ?? "");
        }
        var low = 0; var high = lines.Count - 1; var index = -1;
        while (low <= high) { var middle = low + (high - low) / 2; if (lines[middle].Seconds <= seconds) { index = middle; low = middle + 1; } else high = middle - 1; }
        return (index > 0 ? lines[index - 1].Text : "", index >= 0 ? lines[index].Text : "♪", index + 1 < lines.Count ? lines[index + 1].Text : "");
    }
    internal static List<LyricLine> Parse(string lrc, CancellationToken token = default) => LyricsBudget.Parse(lrc, token);
    public void Dispose() { if (_disposed) return; _disposed = true; _generation++; CancelPending(); _client.Dispose(); }
}
