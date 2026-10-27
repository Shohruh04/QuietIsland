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
    public void Import(string path)
    {
        if (_disposed) return;
        try
        {
            if (_key.Length == 0) { Document = new("Сначала выберите песню", new()); Changed?.Invoke(); return; }
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Файл текста слишком большой");
            var lines = Parse(File.ReadAllText(path));
            if (lines.Count == 0) { Document = new("В файле нет временных меток", new()); Changed?.Invoke(); return; }
            var version = ++_generation; CancelPending();
            Document = new("Локальный синхронный текст", lines);
            _options = new(); Candidates = Array.Empty<LyricsCandidate>();
            Changed?.Invoke();
            _ = SaveCacheAsync(_key, version, new(Document, _options));
        }
        catch (Exception ex) { AppLog.Write(ex); }
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
                    using var json = JsonDocument.Parse(await search.Content.ReadAsStringAsync(token));
                    if (json.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var item in json.RootElement.EnumerateArray().Take(20))
                        { var parsed = ReadCandidate(item); if (parsed != null) options.Add(parsed); }
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    var parsed = ReadCandidate(json.RootElement);
                    if (parsed != null) options.Add(parsed);
                }
                if (!Current(key, version, token)) return;
                options = options.OrderByDescending(o => IsAutomaticMatch(o.Candidate, track))
                    .ThenBy(o => track.Duration > 0 ? Math.Abs(o.Candidate.Duration - track.Duration) : 0).ToList();
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
    private static CachedLyricsCandidate? ReadCandidate(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        var title = Text(root, "trackName"); if (title.Length == 0) title = Text(root, "name");
        var artist = Text(root, "artistName");
        var duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var seconds) && double.IsFinite(seconds) ? seconds : 0;
        var instrumental = root.TryGetProperty("instrumental", out var ins) && ins.ValueKind == JsonValueKind.True;
        var lines = Parse(Text(root, "syncedLyrics")); var plain = Text(root, "plainLyrics");
        if (!instrumental && lines.Count == 0 && string.IsNullOrWhiteSpace(plain)) return null;
        var status = instrumental ? "Инструментальная композиция" : lines.Count > 0 ? "Синхронный текст · LRCLIB" : "Текст без временных меток";
        return new(new(title, artist, Math.Max(0, duration), Text(root, "albumName"), lines.Count > 0), new(status, lines, plain));
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
    private static async Task<LyricsCacheEntry?> ReadCacheAsync(string key, CancellationToken token)
    {
        var path = CachePath(key);
        if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= TimeSpan.FromDays(30) || new FileInfo(path).Length > 1024 * 1024) return null;
        try
        {
            var data = await File.ReadAllTextAsync(path, token);
            using var json = JsonDocument.Parse(data);
            if (json.RootElement.TryGetProperty("Lines", out _))
            {
                var old = JsonSerializer.Deserialize<LyricsDocument>(data);
                return old == null ? null : new(old, new());
            }
            var entry = JsonSerializer.Deserialize<LyricsCacheEntry>(data);
            return entry?.Options != null ? entry : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
    private async Task SaveCacheAsync(string key, int version, LyricsCacheEntry entry)
    {
        string? temporary = null;
        await _cacheGate.WaitAsync();
        try
        {
            if (_disposed || version != _generation || key != _key) return;
            Directory.CreateDirectory(CacheDirectory);
            var path = CachePath(key);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entry));
            if (_disposed || version != _generation || key != _key) return;
            File.Move(temporary, path, true); temporary = null;
            foreach (var old in new DirectoryInfo(CacheDirectory).GetFiles("*.json")
                .Where(f => Regex.IsMatch(f.Name, @"^[A-F0-9]{64}\.json$"))
                .OrderByDescending(f => f.LastWriteTimeUtc).Skip(100)) old.Delete();
        }
        catch (Exception ex) { AppLog.Write(new IOException("Ошибка кеша текста: " + ex.GetType().Name)); }
        finally
        {
            if (temporary != null) { try { File.Delete(temporary); } catch { } }
            _cacheGate.Release();
        }
    }
    private bool Current(string key, int version, CancellationToken token) => !_disposed && key == _key && version == _generation && !token.IsCancellationRequested;
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
    internal static List<LyricLine> Parse(string lrc)
    {
        var result = new List<LyricLine>();
        var offsetMatch = Regex.Match(lrc, @"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase);
        var offset = offsetMatch.Success && int.TryParse(offsetMatch.Groups[1].Value, out var ms) ? ms / 1000.0 : 0;
        foreach (var line in lrc.Split('\n'))
        {
            var stamps = Regex.Matches(line, @"\[(\d{1,3}):(\d{2}(?:[.,]\d{1,3})?)\]");
            if (stamps.Count == 0) continue;
            var text = Regex.Replace(line, @"\[[^\]]*\]", "").Trim();
            foreach (Match stamp in stamps)
                if (double.TryParse(stamp.Groups[2].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                    result.Add(new(Math.Max(0, int.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture) * 60 + seconds - offset), text));
        }
        return result.OrderBy(line => line.Seconds).ToList();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _generation++; CancelPending(); _client.Dispose(); }
}
