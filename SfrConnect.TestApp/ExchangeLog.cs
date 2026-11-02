namespace SfrConnect.TestApp;

// USB callbacks only update bounded storage; the UI renders a batch on its own timer.
internal sealed class ExchangeLog
{
    private const int Capacity = 500;
    private readonly object sync = new();
    private readonly Queue<string> lines = new();
    private bool changed;

    public void Add(string message, DateTime timestamp)
    {
        lock (sync)
        {
            lines.Enqueue($"[{timestamp:HH:mm:ss.fff}] {message}");
            while (lines.Count > Capacity) lines.Dequeue();
            changed = true;
        }
    }

    public string? TakeUpdate()
    {
        lock (sync)
        {
            if (!changed) return null;
            changed = false;
            return string.Join(Environment.NewLine, lines);
        }
    }

    public string Snapshot()
    {
        lock (sync) return string.Join(Environment.NewLine, lines);
    }
}
