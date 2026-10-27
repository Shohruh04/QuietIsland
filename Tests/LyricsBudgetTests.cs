using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace QuietIsland.Tests;

[TestClass]
public sealed class LyricsBudgetTests
{
    [TestMethod]
    public void RejectsSmallInputWithLargeTimestampExpansion()
    {
        var input = string.Concat(Enumerable.Repeat("[00:01.00]", 100)) + new string('a', 1500);
        Assert.IsLessThan(4096, input.Length);
        Assert.ThrowsExactly<InvalidDataException>(() => LyricsService.Parse(input));
    }

    [TestMethod]
    public void RejectsTooManyEntriesEvenWithEmptyText()
    {
        var input = string.Concat(Enumerable.Repeat("[00:01]", LyricsBudget.MaximumEntries + 1));
        Assert.ThrowsExactly<InvalidDataException>(() => LyricsService.Parse(input));
    }

    [TestMethod]
    public void RejectsOversizedLineAndUnterminatedBracketSuffix()
    {
        // Modest fixtures exercise the previously quadratic suffix without executing a huge exploit.
        Assert.ThrowsExactly<InvalidDataException>(() => LyricsService.Parse("[00:01]" + new string('[', 16 * 1024)));
        Assert.ThrowsExactly<InvalidDataException>(() => LyricsService.Parse("[00:01]" + new string('a', LyricsBudget.MaximumLineCharacters + 1)));
        Assert.AreEqual("text[unfinished", LyricsService.Parse("[00:01][ar:artist]text[unfinished")[0].Text);
    }

    [TestMethod]
    public void NormalMultipleTimestampsOffsetsAndUnterminatedTextRemainCompatible()
    {
        var lines = LyricsService.Parse("[offset:-100]\r\n[00:01.25][00:03,5]One [tag]line\n[00:02]Two [open");
        Assert.AreEqual(3, lines.Count);
        Assert.AreEqual(1.35, lines[0].Seconds, .001);
        Assert.AreEqual(3.6, lines[2].Seconds, .001);
        Assert.AreEqual("One line", lines[0].Text);
        Assert.AreEqual("Two [open", lines[1].Text);
    }

    [TestMethod]
    public void CancelledParsingStopsBeforeProcessing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => LyricsService.Parse("[00:01]line", cancellation.Token));
    }

    [TestMethod]
    public async Task RawReadDetectsUtf8Utf16AndUtf32Bom()
    {
        const string text = "[00:01]Привет ♪";
        foreach (var encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32 })
        {
            using var input = new MemoryStream(encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray());
            Assert.AreEqual(text, await LyricsBudget.ReadTextAsync(input, CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task RawReadEnforcesActualBytesInsteadOfAdvertisedLength()
    {
        using var input = new LyingLengthStream(new byte[LyricsBudget.MaximumBytes + 1]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LyricsBudget.ReadTextAsync(input, CancellationToken.None));
        using var exact = new MemoryStream(new byte[LyricsBudget.MaximumBytes]);
        Assert.AreEqual(LyricsBudget.MaximumBytes, (await LyricsBudget.ReadTextAsync(exact, CancellationToken.None)).Length);
    }

    [TestMethod]
    public async Task RawReadHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("[00:01]line"));
        await Assert.ThrowsAsync<OperationCanceledException>(() => LyricsBudget.ReadTextAsync(input, cancellation.Token));
    }

    [TestMethod]
    public async Task SerializedByteBudgetCountsSelectedAndAllOptionsIncludingEscaping()
    {
        var lines = Enumerable.Range(0, 100).Select(index => new LyricLine(index, new string('\u0001', 1000))).ToList();
        var document = new LyricsDocument("local", lines);
        var option = new CachedLyricsCandidate(new("title", "artist", 200, Synced: true), document);
        Assert.IsTrue(LyricsBudget.ValidCache(new(document, new() { option })));
        using var single = new MemoryStream();
        await LyricsBudget.WriteCacheAsync(single, new(document, new()));
        Assert.IsLessThan(LyricsBudget.MaximumBytes, single.Length);
        using var duplicated = new MemoryStream();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LyricsBudget.WriteCacheAsync(duplicated, new(document, new() { option })));
        Assert.IsLessThanOrEqualTo(LyricsBudget.MaximumBytes, duplicated.Length);
        using var optionsOnly = new MemoryStream();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LyricsBudget.WriteCacheAsync(optionsOnly, new(null, new() { option, option })));
        Assert.IsLessThanOrEqualTo(LyricsBudget.MaximumBytes, optionsOnly.Length);
    }

    [TestMethod]
    public async Task NormalCacheRoundTripsAndOutputOwnershipStaysWithCaller()
    {
        var document = new LyricsDocument("local", LyricsService.Parse("[00:01]First\n[00:02]Second"));
        using var output = new MemoryStream();
        await LyricsBudget.WriteCacheAsync(output, new(document, new()));
        Assert.IsTrue(output.CanWrite);
        output.Position = 0;
        var restored = await JsonSerializer.DeserializeAsync<LyricsCacheEntry>(output);
        Assert.IsNotNull(restored);
        Assert.IsTrue(LyricsBudget.ValidCache(restored));
        Assert.AreEqual("Second", restored.Selected!.Lines[1].Text);
    }

    [TestMethod]
    public void CacheValidationRejectsMalformedAndOversizedDocuments()
    {
        Assert.IsFalse(LyricsBudget.ValidCache(new(new("status", new() { new(double.NaN, "text") }), new())));
        Assert.IsFalse(LyricsBudget.ValidCache(new(new("status", null!), new())));
        Assert.IsFalse(LyricsBudget.ValidCache(new(new("status", new(), new string('a', LyricsBudget.MaximumExpandedCharacters + 1)), new())));
        Assert.IsFalse(LyricsBudget.ValidCache(new(null, new() { null! })));
    }

    private sealed class LyingLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override long Length => 0;
    }
}
