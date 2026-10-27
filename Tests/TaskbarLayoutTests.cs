using System;
using System.Linq;
using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace QuietIsland.Tests;

[TestClass]
public sealed class TaskbarLayoutTests
{
    private static NativeWindow.Rect Bar => new() { Left = 0, Top = 1056, Right = 1920, Bottom = 1080 };

    [TestMethod]
    [DataRow("left")]
    [DataRow("right")]
    public void PlacementAvoidsEveryButtonAndUsesFullHeight(string mode)
    {
        var buttons = new[] { new Rect(700, 1056, 550, 24), new Rect(2, 1056, 50, 24) };
        var position = TaskbarLayout.Choose(Bar, 1700, buttons, 1, mode);
        Assert.IsTrue(position.HasValue);
        Assert.IsTrue(buttons.All(button => !button.IntersectsWith(position.Value)));
        Assert.AreEqual((double)Bar.Height, position.Value.Height);
        Assert.AreEqual((double)Bar.Top, position.Value.Top);
    }

    [TestMethod]
    public void LeftPlacementDoesNotLeaveAnEmptyEdgeMargin()
    {
        Assert.AreEqual(0d, TaskbarLayout.Choose(Bar, 1700,
            new[] { new Rect(700, 1056, 550, 24) }, 1, "left")?.Left);
    }

    [TestMethod]
    [DataRow("left")]
    [DataRow("center")]
    [DataRow("right")]
    [DataRow("custom")]
    public void LeftAlignedStartAndAppsRemainUncovered(string mode)
    {
        var button = new Rect(0, 1056, 550, 24);
        var position = TaskbarLayout.Choose(Bar, 1700, new[] { button }, 1, mode, 480, 0);
        Assert.IsTrue(position.HasValue);
        Assert.IsTrue(position.Value.Left >= 550 && !position.Value.IntersectsWith(button));
    }

    [TestMethod]
    [DataRow(0d)]
    [DataRow(.25)]
    [DataRow(.5)]
    [DataRow(.75)]
    [DataRow(1d)]
    public void CustomPositionAlwaysUsesSafeGap(double fraction)
    {
        var buttons = new[] { new Rect(300, 1056, 100, 24), new Rect(1000, 1056, 100, 24) };
        var position = TaskbarLayout.Choose(Bar, 1700, buttons, 1, "custom", 480, fraction);
        Assert.IsTrue(position.HasValue);
        Assert.IsTrue(buttons.All(button => !button.IntersectsWith(position.Value)));
    }

    [TestMethod]
    public void ScaledTaskbarUsesPhysicalPixels()
    {
        var bar = new NativeWindow.Rect { Left = 0, Top = 2064, Right = 3840, Bottom = 2160 };
        var position = TaskbarLayout.Choose(bar, 3400, new[] { new Rect(1800, 2064, 800, 96) }, 2, "left");
        Assert.AreEqual(96d, position?.Height);
        Assert.AreEqual(960d, position?.Width);
    }

    [TestMethod]
    public void CustomWidthIsClippedToAvailableSpace()
    {
        var buttons = new[] { new Rect(700, 1056, 550, 24) };
        Assert.AreEqual(600d, TaskbarLayout.Choose(Bar, 1700, buttons, 1, "left", 600)?.Width);
        var clipped = TaskbarLayout.Choose(Bar, 1700, buttons, 1, "left", 1200);
        Assert.IsTrue(clipped?.Width < 700);
        Assert.AreEqual((double)Bar.Height, clipped?.Height);
    }

    [TestMethod]
    public void CrowdedTaskbarHidesInsteadOfCoveringApps()
    {
        Assert.IsNull(TaskbarLayout.Choose(Bar, 1700, new[] { new Rect(0, 1056, 1700, 24) }, 1, "left"));
    }

    [TestMethod]
    public void NegativeMonitorCoordinatesAreSupported()
    {
        var bar = new NativeWindow.Rect { Left = -1920, Top = 1056, Right = 0, Bottom = 1080 };
        Assert.IsTrue(TaskbarLayout.Choose(bar, -200, new[] { new Rect(-1200, 1056, 500, 24) }, 1, "left")?.Left < 0);
    }

    [TestMethod]
    public void TransientScanFailureKeepsPlacementForOnlyTwoSeconds()
    {
        using var scan = new TaskbarSpace();
        var time = DateTimeOffset.UnixEpoch;
        var buttons = new[] { new Rect(700, 1056, 550, 24) };
        scan.AcceptScan(new IntPtr(1), Bar, 1700, buttons, time);
        scan.AcceptScan(new IntPtr(1), Bar, 1700, null, time.AddSeconds(30));
        Assert.IsTrue(scan.Valid);
        Assert.AreEqual(buttons.Length, scan.Buttons.Count);
        scan.AcceptScan(new IntPtr(1), Bar, 1700, null, time.AddSeconds(32));
        Assert.IsFalse(scan.Valid);
    }

    [TestMethod]
    public void ExplorerReplacementInvalidatesCachedPlacement()
    {
        using var scan = new TaskbarSpace();
        var time = DateTimeOffset.UnixEpoch;
        scan.AcceptScan(new IntPtr(1), Bar, 1700, new[] { new Rect(700, 1056, 550, 24) }, time);
        scan.AcceptScan(new IntPtr(2), Bar, 1700, null, time.AddMilliseconds(100));
        Assert.IsFalse(scan.Valid);
    }

    [TestMethod]
    public void DisplayChangeInvalidatesCachedPlacement()
    {
        using var scan = new TaskbarSpace();
        var time = DateTimeOffset.UnixEpoch;
        scan.AcceptScan(new IntPtr(1), Bar, 1700, new[] { new Rect(700, 1056, 550, 24) }, time);
        var changed = new NativeWindow.Rect { Left = 0, Top = 2064, Right = 3840, Bottom = 2160 };
        scan.AcceptScan(new IntPtr(1), changed, 3400, null, time.AddMilliseconds(100));
        Assert.IsFalse(scan.Valid);
    }
}
