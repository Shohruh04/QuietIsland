using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace QuietIsland.Tests;

[TestClass]
public sealed class EdgePinTests
{
    [TestMethod]
    [DataRow(-100, "left")]
    [DataRow(8, "left")]
    [DataRow(500, "custom")]
    [DataRow(1888, "right")]
    [DataRow(2100, "right")]
    public void PinsNearOrBeyondSafeEdges(int desired, string expected)
    {
        Assert.AreEqual(expected, TaskbarLayout.GetPinMode(desired, new Rect(0, 100, 300, 48), new Rect(1900, 100, 300, 48), 1));
    }
    [TestMethod]
    public void PinThresholdScalesWithDpiAndRespectsLeftStartButtons()
    {
        var safeLeft = new Rect(600, 100, 600, 96);
        Assert.AreEqual("left", TaskbarLayout.GetPinMode(630, safeLeft, new Rect(2000, 100, 600, 96), 2));
        Assert.AreEqual("custom", TaskbarLayout.GetPinMode(630, safeLeft, new Rect(2000, 100, 600, 96), 1));
        Assert.AreEqual("custom", TaskbarLayout.GetPinMode(0, null, null, 1));
    }
}
