using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace QuietIsland.Tests;

[TestClass]
public sealed class DiagnosticsTests
{
    [TestMethod]
    public void NativePlacementCoordinatesReplaceUninitializedWpfValues()
    {
        Assert.AreEqual(-1920d, WindowDiagnostics.Finite(double.NaN, -1920));
        Assert.AreEqual(1032d, WindowDiagnostics.Finite(double.PositiveInfinity, 1032));
        Assert.AreEqual(12.5d, WindowDiagnostics.Finite(12.5, 0));
    }
}
