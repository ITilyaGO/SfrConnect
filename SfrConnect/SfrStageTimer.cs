using System.Diagnostics;

namespace SfrConnect;

// Stage messages share the existing diagnostic sink without adding USB exchanges.
internal sealed class SfrStageTimer : IDisposable
{
    private readonly Action<string>? log;
    private string name = "";
    private long started;
    private bool active;

    internal SfrStageTimer(Action<string>? log, string name)
    {
        this.log = log;
        Next(name);
    }

    internal void Next(string nextName)
    {
        Finish();
        name = nextName;
        started = Stopwatch.GetTimestamp();
        active = true;
        log?.Invoke($"ЭТАП · {name} · начало");
    }

    internal void Finish(bool success = true)
    {
        if (!active) return;
        var elapsed = Stopwatch.GetElapsedTime(started);
        active = false;
        log?.Invoke($"ЭТАП · {name} · {(success ? "готово" : "прервано")} · {elapsed.TotalMilliseconds:F0} мс ({elapsed.TotalSeconds:F3} с)");
    }

    public void Dispose() => Finish(success: false);
}
