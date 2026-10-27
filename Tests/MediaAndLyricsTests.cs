using System;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace QuietIsland.Tests;

[TestClass]
public sealed class MediaAndLyricsTests
{
    [TestMethod]
    [DataRow(-100d, 0d)]
    [DataRow(500d, 180d)]
    public void SeekClampsOutsideTrack(double pointer, double expected)
        => Assert.AreEqual(expected, IslandWindow.SeekPosition(pointer, 400, 180));

    [TestMethod]
    [DataRow(.1)]
    [DataRow(.5)]
    [DataRow(.9)]
    public void SeekMatchesPointerAcrossTrack(double fraction)
        => Assert.AreEqual(fraction * 180, IslandWindow.SeekPosition(2.5 + fraction * 395, 400, 180), .001);

    [TestMethod]
    public void SeekRejectsCachedOldTimeline()
    {
        var time = DateTimeOffset.UnixEpoch;
        Assert.IsFalse(MediaService.ConfirmsSeek(16, time.AddSeconds(-1), 78, time.AddSeconds(-1), time, time.AddMilliseconds(100), 1));
    }

    [TestMethod]
    public void SeekConfirmsBeforeApiCompletion()
    {
        var time = DateTimeOffset.UnixEpoch;
        Assert.IsTrue(MediaService.ConfirmsSeek(78.1, time.AddMilliseconds(100), 78, time.AddSeconds(-1), time, time.AddMilliseconds(300), 1));
    }

    [TestMethod]
    public void SeekConfirmsBackwardWhilePaused()
    {
        var time = DateTimeOffset.UnixEpoch;
        Assert.IsTrue(MediaService.ConfirmsSeek(10, time.AddMilliseconds(50), 10, time.AddSeconds(-1), time, time.AddMilliseconds(300), 0));
    }

    [TestMethod]
    public void LabelsSupportEnglishAndRussian()
    {
        Assert.AreEqual("Background transparency", UiText.Translate("Прозрачность фона", "en"));
        Assert.AreEqual("Прозрачность фона", UiText.Translate("Прозрачность фона", "ru"));
    }

    [TestMethod]
    public void MissingThumbnailRetainsOnlySameTrackArtwork()
    {
        var cover = new DrawingImage(); cover.Freeze();
        var track = new MediaState("Song", "Artist", Available: true, Artwork: cover, Album: "Album");
        Assert.AreSame(cover, MediaService.ResolveArtwork(track, "Song", "Artist", "Album", null));
        Assert.IsNull(MediaService.ResolveArtwork(track, "Another song", "Artist", "Album", null));
        Assert.IsNull(MediaService.ResolveArtwork(track, "Song", "Artist", "Different album", null));
    }

    [TestMethod]
    public void LyricsParseMultipleStampsOffsetAndSort()
    {
        var lyrics = LyricsService.Parse("[offset:100]\n[00:01.50][00:03.25]Test line\n[00:02,00]Another line");
        Assert.AreEqual(3, lyrics.Count);
        Assert.AreEqual(1.4, lyrics[0].Seconds, .001);
        Assert.AreEqual(3.15, lyrics[2].Seconds, .001);
        Assert.AreEqual("Another line", lyrics[1].Text);
    }

    [TestMethod]
    public void AutomaticLyricsMatchNormalizesMetadataButRejectsWrongTracks()
    {
        var track = new MediaState(Title: "Test Song!", Artist: "By Artist, Guest", Available: true, Duration: 180);
        Assert.IsTrue(LyricsService.IsAutomaticMatch(new LyricsCandidate("Test Song", "Artist", 182), track));
        Assert.IsFalse(LyricsService.IsAutomaticMatch(new LyricsCandidate("Test Song", "Artist", 210), track));
        Assert.IsFalse(LyricsService.IsAutomaticMatch(new LyricsCandidate("Test Song", "Someone Else", 180), track));
    }
}
