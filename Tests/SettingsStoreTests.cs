using System;
using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace QuietIsland.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    private string _directory = null!;
    private JsonSettingsStore _store = null!;

    [TestInitialize]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "QuietIsland.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _store = new JsonSettingsStore(_directory, _ => { });
    }

    [TestCleanup]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    public void MissingSettingsUseExistingDefaults()
    {
        var settings = _store.Load();
        Assert.AreEqual("ru", settings.Language);
        Assert.AreEqual("left", settings.Placement);
        Assert.AreEqual("obsidian", settings.Theme);
        Assert.AreEqual(480, settings.WidthDip);
        Assert.AreEqual(90, settings.BackgroundOpacityPercent);
        Assert.AreEqual(.5, settings.PositionFraction);
        Assert.IsTrue(settings.TaskbarPlacement && settings.VolumeAlerts && settings.OnlineLyrics && settings.ShowLyrics && settings.HideInFullscreen);
        Assert.IsFalse(settings.Hidden);
    }

    [TestMethod]
    public void ExistingPartialSettingsKeepValuesAndDefaultMissingProperties()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"),
            """{"Placement":"right","Language":"en","WidthDip":640,"Hidden":true,"FutureOption":"ignored"}""");
        var settings = _store.Load();
        Assert.AreEqual("right", settings.Placement);
        Assert.AreEqual("en", settings.Language);
        Assert.AreEqual(640, settings.WidthDip);
        Assert.IsTrue(settings.Hidden);
        Assert.IsTrue(settings.OnlineLyrics && settings.ShowLyrics);
        Assert.AreEqual(90, settings.BackgroundOpacityPercent);
    }

    [TestMethod]
    [DataRow("{broken")]
    [DataRow("null")]
    [DataRow("{\"WidthDip\":\"invalid\"}")]
    public void InvalidSettingsRecoverWithDefaults(string json)
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), json);
        Assert.AreEqual(480, _store.Load().WidthDip);
    }

    [TestMethod]
    public void EveryExistingPropertyRoundTripsWithoutSchemaChanges()
    {
        var settings = new AppSettings
        {
            Hidden = true, TaskbarPlacement = false, VolumeAlerts = false, OnlineLyrics = false,
            ShowLyrics = false, HideInFullscreen = false, Placement = "custom", LyricsOffsetMs = -700,
            Theme = "pearl", BackgroundOpacityPercent = 73, MonitorDevice = @"\\.\DISPLAY2",
            Language = "en", WidthDip = 760, PositionFraction = .23
        };
        _store.Save(settings);
        Assert.AreEqual(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(_store.Load()));
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "settings.json")));
        Assert.AreEqual(14, document.RootElement.GetPropertyCount());
    }

    [TestMethod]
    public void SaveReplacesPreviousFileWithoutLeavingTemporaryFiles()
    {
        _store.Save(new AppSettings { WidthDip = 600 });
        _store.Save(new AppSettings { WidthDip = 800 });
        Assert.AreEqual(800, _store.Load().WidthDip);
        CollectionAssert.AreEqual(new[] { "settings.json" }, Array.ConvertAll(Directory.GetFiles(_directory), Path.GetFileName));
    }

    [TestMethod]
    public void FailedSerializationPreservesPreviousFile()
    {
        _store.Save(new AppSettings { WidthDip = 600 });
        var before = File.ReadAllText(Path.Combine(_directory, "settings.json"));
        _store.Save(new AppSettings { PositionFraction = double.NaN });
        Assert.AreEqual(before, File.ReadAllText(Path.Combine(_directory, "settings.json")));
        Assert.AreEqual(1, Directory.GetFiles(_directory).Length);
    }

    [TestMethod]
    public void LockedDestinationPreservesPreviousFileAndCleansTemporaryFile()
    {
        _store.Save(new AppSettings { WidthDip = 600 });
        using (File.Open(Path.Combine(_directory, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            _store.Save(new AppSettings { WidthDip = 800 });
        Assert.AreEqual(600, _store.Load().WidthDip);
        Assert.AreEqual(1, Directory.GetFiles(_directory).Length);
    }
}
