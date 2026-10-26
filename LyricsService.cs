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

internal sealed class LyricsService : IDisposable
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1024 * 1024 };
    private CancellationTokenSource? _pending;
    private readonly SemaphoreSlim _gate = new(1);
    private string _key = "";
    private DateTimeOffset _retryAfter;
    public LyricsDocument Document { get; private set; } = new("Включите музыку", new());
    public event Action? Changed;
    public void Retry(MediaState state, bool enabled) { _key = ""; Update(state, enabled); }
    public void Import(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Файл текста слишком большой");
            var lines = Parse(File.ReadAllText(path));
            _pending?.Cancel(); Document = new(lines.Count > 0 ? "Локальный синхронный текст" : "В файле нет временных меток", lines); Changed?.Invoke();
        }
        catch (Exception ex) { AppSettings.Log(ex); }
    }
    public LyricsService() => _client.DefaultRequestHeaders.UserAgent.ParseAdd("QuietIsland/1.0 (https://github.com/Shohruh04/QuietIsland)");

    public void Update(MediaState track, bool enabled)
    {
        var key = enabled && track.Available ? track.Title + "\n" + track.Artist + "\n" + track.Album + "\n" + Math.Round(track.Duration) : enabled ? "empty" : "disabled";
        if (_key == key) return;
        _key = key; _pending?.Cancel(); _pending?.Dispose(); _pending = null;
        if (!enabled || !track.Available) { Document = new(enabled ? "Включите музыку" : "Онлайн-текст отключён", new()); Changed?.Invoke(); return; }
        _pending = new CancellationTokenSource();
        _ = LoadAsync(track, key, _pending.Token);
    }
    private async Task LoadAsync(MediaState track, string key, CancellationToken token)
    {
        Document = new("Поиск текста песни…", new()); Changed?.Invoke();
        var cacheDir = Path.Combine(AppSettings.DataDirectory, "Lyrics");
        var cache = Path.Combine(cacheDir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
        try
        {
            if (File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromDays(30))
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<LyricsDocument>(await File.ReadAllTextAsync(cache, token));
                    if (cached != null && key == _key && !token.IsCancellationRequested) { Document = cached; Changed?.Invoke(); return; }
                }
                catch (JsonException) { }
            }
            await Task.Delay(500, token); // Debounce rapidly changing sessions/tracks.
            await _gate.WaitAsync(token);
            try
            {
                var wait = _retryAfter - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) { Publish(key, new("Сервис текста занят. Повторите позже.", new()), token); return; }
                var query = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(track.Title) + "&artist_name=" + Uri.EscapeDataString(track.Artist);
                if (!string.IsNullOrWhiteSpace(track.Album)) query += "&album_name=" + Uri.EscapeDataString(track.Album);
                if (track.Duration is >= 1 and <= 3600) query += "&duration=" + Math.Round(track.Duration).ToString(CultureInfo.InvariantCulture);
                using var response = await _client.GetAsync(query, token);
                _retryAfter = DateTimeOffset.UtcNow.AddMilliseconds(500);
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    _retryAfter = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                    Publish(key, new("Сервис текста временно недоступен", new()), token); return;
                }
                if (response.StatusCode == HttpStatusCode.NotFound) { Publish(key, new("Текст этой песни не найден", new()), token); return; }
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var root = json.RootElement;
                var instrumental = root.TryGetProperty("instrumental", out var ins) && ins.ValueKind == JsonValueKind.True;
                var sync = root.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                var plain = root.TryGetProperty("plainLyrics", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                var lines = Parse(sync ?? "");
                var document = new LyricsDocument(instrumental ? "Инструментальная композиция" : lines.Count > 0 ? "Синхронный текст · LRCLIB" : string.IsNullOrWhiteSpace(plain) ? "Текст не найден" : "Текст без временных меток", lines, plain);
                Publish(key, document, token);
                Directory.CreateDirectory(cacheDir);
                await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(document), token);
                // Bound the disk cache. Only our hashed JSON entries are considered.
                foreach (var old in new DirectoryInfo(cacheDir).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(100)) old.Delete();
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested) Publish(key, new("Сервис текста не ответил", new()), token); }
        catch (Exception ex) { AppSettings.Log(ex); Publish(key, new("Не удалось загрузить текст песни", new()), token); }
    }
    private void Publish(string key, LyricsDocument document, CancellationToken token)
    { if (key == _key && !token.IsCancellationRequested) { Document = document; Changed?.Invoke(); } }

    public (string previous, string current, string next) At(double seconds)
    {
        var lines = Document.Lines;
        if (lines.Count == 0)
        {
            var plain = Document.Plain?.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return ("", plain?.FirstOrDefault() ?? Document.Status, plain?.Skip(1).FirstOrDefault() ?? "");
        }
        var index = lines.FindLastIndex(line => line.Seconds <= seconds);
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
    public void Dispose() { _pending?.Cancel(); _pending?.Dispose(); _client.Dispose(); }
}
