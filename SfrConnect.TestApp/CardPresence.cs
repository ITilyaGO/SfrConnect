namespace SfrConnect.TestApp;

// A removal must be observed; matching numbers and markers do not identify a physical card.
internal sealed class CardPresence
{
    private int absentSamples;
    public SfrCardNumber? Current { get; private set; }
    public bool RequiresRemoval { get; private set; }
    public bool CanWrite => Current is not null && !RequiresRemoval;

    public void Observe(SfrCardNumber? card)
    {
        Current = card;
        if (card is not null)
        {
            absentSamples = 0;
            return;
        }
        absentSamples = Math.Min(absentSamples + 1, 2);
        if (absentSamples >= 2)
        {
            RequiresRemoval = false;
        }
    }

    public void BeginWrite()
    {
        if (!CanWrite)
        {
            throw new InvalidOperationException("Приложите чип; после записи сначала уберите предыдущий.");
        }
        MarkWriteAttempt();
    }

    public void MarkWriteAttempt()
    {
        RequiresRemoval = true;
        absentSamples = 0;
    }

    public void Invalidate()
    {
        Current = null;
        absentSamples = 0;
    }
}
