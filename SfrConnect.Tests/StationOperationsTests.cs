using SfrConnect.TestApp;

namespace SfrConnect.Tests;

public class StationOperationsTests
{
    [Fact]
    public async Task PollingNeverDisablesInteractiveControls()
    {
        var queue = new StationOperations();
        var gate = new TaskCompletionSource();
        var states = new List<bool>();
        queue.Changed += () => states.Add(queue.IsInteractiveBusy);
        var poll = queue.RunAsync(() => gate.Task, background: true);
        Assert.True(queue.IsBusy);
        Assert.False(queue.IsInteractiveBusy);
        gate.SetResult();
        await poll;
        Assert.False(queue.IsBusy);
        Assert.All(states, state => Assert.False(state));
    }

    [Fact]
    public async Task ClickWaitsForPollAndSuppressesDuplicateClicksAndPolls()
    {
        var queue = new StationOperations();
        var pollGate = new TaskCompletionSource();
        var commandGate = new TaskCompletionSource();
        var order = new List<string>();
        var poll = queue.RunAsync(async () => { await pollGate.Task; order.Add("read"); }, true);
        var command = queue.RunAsync(async () => { order.Add("write"); await commandGate.Task; });
        Assert.True(queue.IsInteractiveBusy);
        Assert.Same(command, queue.Pending);
        await queue.RunAsync(() => throw new Exception("Duplicate click must not run"));
        await queue.RunAsync(() => throw new Exception("Overlapping poll must not run"), true);
        Assert.Empty(order);
        pollGate.SetResult();
        await poll;
        commandGate.SetResult();
        await command;
        Assert.Equal(new[] { "read", "write" }, order);
        Assert.False(queue.IsBusy);
    }

    [Fact]
    public async Task PollsDoNotOverlap()
    {
        var queue = new StationOperations();
        var gate = new TaskCompletionSource();
        var poll = queue.RunAsync(() => gate.Task, true);
        await queue.RunAsync(() => throw new Exception("Overlapping poll"), true);
        gate.SetResult();
        await poll;
    }
}
