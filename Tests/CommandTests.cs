using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace QuietIsland.Tests;

[TestClass]
public sealed class CommandTests
{
    [TestMethod]
    public async Task DisabledCommandDoesNotExecute()
    {
        var calls = 0;
        var command = new AsyncActionCommand(() => { calls++; return Task.CompletedTask; }, () => false);
        await command.ExecuteAsync();
        Assert.AreEqual(0, calls);
    }
    [TestMethod]
    public async Task RepeatedClickCannotOverlapTheSameCommand()
    {
        var calls = 0;
        var completion = new TaskCompletionSource<bool>();
        var command = new AsyncActionCommand(() => { calls++; return completion.Task; }, () => true);
        var first = command.ExecuteAsync();
        Assert.IsFalse(command.CanExecute(null));
        await command.ExecuteAsync();
        Assert.AreEqual(1, calls);
        completion.SetResult(true); await first;
        Assert.IsTrue(command.CanExecute(null));
    }
}
