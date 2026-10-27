using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace QuietIsland.Tests;

[TestClass]
public sealed class FocusTimerTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    [TestMethod]
    public void PauseRetainsRemainingTimeAndResumeUsesNewDeadline()
    {
        var clock = new Clock(); var timer = new FocusTimer(clock);
        timer.Start(TimeSpan.FromMinutes(5)); clock.Advance(TimeSpan.FromMinutes(1)); timer.TogglePause();
        clock.Advance(TimeSpan.FromHours(2)); Assert.AreEqual(TimeSpan.FromMinutes(4), timer.Remaining);
        timer.TogglePause(); clock.Advance(TimeSpan.FromMinutes(4));
        Assert.IsTrue(timer.Tick()); Assert.IsFalse(timer.Tick()); Assert.IsFalse(timer.Active);
    }
    [TestMethod]
    public void StopClearsRunningAndPausedTimers()
    {
        var timer = new FocusTimer(new Clock()); timer.Start(TimeSpan.FromMinutes(25)); timer.TogglePause(); timer.Stop();
        Assert.IsFalse(timer.Active); Assert.AreEqual(TimeSpan.Zero, timer.Remaining);
    }
}
