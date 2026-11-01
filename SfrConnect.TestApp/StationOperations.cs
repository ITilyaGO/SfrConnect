namespace SfrConnect.TestApp;

// Called on the UI thread. Polling serializes USB access without disabling commands.
internal sealed class StationOperations
{
    private bool polling;
    public bool IsInteractiveBusy { get; private set; }
    public bool IsBusy => polling || IsInteractiveBusy;
    public Task Pending { get; private set; } = Task.CompletedTask;
    public event Action? Changed;

    public Task RunAsync(Func<Task> operation, bool background = false)
    {
        if (IsInteractiveBusy || (background && IsBusy)) return Task.CompletedTask;
        var previous = Pending;
        if (background) polling = true;
        else IsInteractiveBusy = true;
        Changed?.Invoke();
        Pending = ExecuteAsync(operation, previous, background);
        return Pending;
    }

    private async Task ExecuteAsync(Func<Task> operation, Task previous, bool background)
    {
        try
        {
            await previous;
            await operation();
        }
        finally
        {
            if (background) polling = false;
            else IsInteractiveBusy = false;
            Changed?.Invoke();
        }
    }
}
