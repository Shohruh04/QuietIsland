using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QuietIsland;

internal static class LyricsBudget
{
    internal const int MaximumBytes = 1024 * 1024;
    internal const int MaximumEntries = 4096;
    internal const int MaximumLineCharacters = 2048;
    internal const int MaximumExpandedCharacters = 128 * 1024;
    private static readonly Regex Offset = new(@"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private static readonly Regex Stamp = new(@"\[(\d{1,3}):(\d{2}(?:[.,]\d{1,3})?)\]", RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    internal static async Task<string> ReadTextAsync(Stream input, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, MaximumBytes - (int)buffer.Length + 1)), token).ConfigureAwait(false);
            if (read == 0) break;
            if (read > MaximumBytes - buffer.Length) throw new InvalidDataException("Lyrics input exceeds the byte budget.");
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(token).ConfigureAwait(false);
    }

    internal static List<LyricLine> Parse(string lrc, CancellationToken token = default)
    {
        if (lrc.Length > MaximumBytes) throw new InvalidDataException("Lyrics input exceeds the character budget.");
        token.ThrowIfCancellationRequested();
        var result = new List<LyricLine>();
        var offsetMatch = Offset.Match(lrc);
        var offset = offsetMatch.Success && int.TryParse(offsetMatch.Groups[1].Value, out var ms) ? ms / 1000.0 : 0;
        var expanded = 0;
        using var reader = new StringReader(lrc);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            token.ThrowIfCancellationRequested();
            string? text = null;
            foreach (Match stamp in Stamp.Matches(line))
            {
                token.ThrowIfCancellationRequested();
                text ??= StripTags(line).Trim();
                if (text.Length > MaximumLineCharacters || result.Count >= MaximumEntries || text.Length > MaximumExpandedCharacters - expanded)
                    throw new InvalidDataException("Lyrics expansion exceeds the presentation budget.");
                if (!double.TryParse(stamp.Groups[2].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                    !int.TryParse(stamp.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)) continue;
                expanded += text.Length;
                result.Add(new(Math.Max(0, minutes * 60 + seconds - offset), text));
            }
        }
        return result.OrderBy(line => line.Seconds).ToList();
    }

    // Match the old bracket-tag removal without retrying an unmatched suffix at every '['.
    private static string StripTags(string line)
    {
        var result = new StringBuilder(Math.Min(line.Length, MaximumLineCharacters + 1));
        var open = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (open >= 0) { if (line[i] == ']') open = -1; }
            else if (line[i] == '[') open = i;
            else result.Append(line[i]);
            if (result.Length > MaximumLineCharacters) throw new InvalidDataException("Lyrics line exceeds the presentation budget.");
        }
        if (open >= 0)
        {
            if (line.Length - open > MaximumLineCharacters - result.Length) throw new InvalidDataException("Lyrics line exceeds the presentation budget.");
            result.Append(line, open, line.Length - open);
        }
        return result.ToString();
    }

    internal static bool ValidDocument(LyricsDocument? document)
    {
        if (document == null) return true;
        if (document.Lines == null || document.Lines.Count > MaximumEntries || document.Status == null || document.Status.Length > MaximumLineCharacters ||
            document.Plain?.Length > MaximumExpandedCharacters) return false;
        var total = document.Plain?.Length ?? 0;
        foreach (var line in document.Lines)
        {
            if (line == null || !double.IsFinite(line.Seconds) || line.Seconds < 0 || line.Text == null || line.Text.Length > MaximumLineCharacters ||
                line.Text.Length > MaximumExpandedCharacters - total) return false;
            total += line.Text.Length;
        }
        return true;
    }

    internal static bool ValidCache(LyricsCacheEntry entry) => entry.Options is { Count: <= 20 } && ValidDocument(entry.Selected) &&
        entry.Options.All(option => option != null && option.Candidate != null && option.Document != null && ValidDocument(option.Document) &&
            double.IsFinite(option.Candidate.Duration) && option.Candidate.Duration >= 0 &&
            ValidLabel(option.Candidate.Title) && ValidLabel(option.Candidate.Artist) && ValidLabel(option.Candidate.Album));
    private static bool ValidLabel(string? value) => value != null && value.Length <= MaximumLineCharacters;

    internal static async Task WriteCacheAsync(Stream output, LyricsCacheEntry entry, CancellationToken token = default)
    {
        if (!ValidCache(entry)) throw new InvalidDataException("Invalid lyrics cache document.");
        using var bounded = new LimitedWriteStream(output, MaximumBytes);
        await JsonSerializer.SerializeAsync(bounded, entry, cancellationToken: token).ConfigureAwait(false);
        await bounded.FlushAsync(token).ConfigureAwait(false);
    }

    private sealed class LimitedWriteStream(Stream inner, int maximum) : Stream
    {
        private long _written;
        private void Reserve(int count) { if (count > maximum - _written) throw new InvalidDataException("Lyrics cache exceeds the byte budget."); _written += count; }
        public override void Write(byte[] buffer, int offset, int count) { Reserve(count); inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Reserve(buffer.Length); inner.Write(buffer); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Reserve(buffer.Length); await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
