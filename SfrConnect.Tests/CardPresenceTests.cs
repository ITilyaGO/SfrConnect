using SfrConnect.TestApp;

namespace SfrConnect.Tests;

public class CardPresenceTests
{
    [Fact]
    public void WritingRequiresAPresentCard()
    {
        var state = new CardPresence();
        Assert.False(state.CanWrite);
        Assert.Throws<InvalidOperationException>(state.BeginWrite);
        state.Observe(new SfrCardNumber(0, "00000000"));
        Assert.True(state.CanWrite);
    }

    [Fact]
    public void ChangedNumberDoesNotRearmWriting()
    {
        var state = Ready();
        state.BeginWrite();
        state.Observe(new SfrCardNumber(39999, "11111111"));
        Assert.False(state.CanWrite);
        Assert.Throws<InvalidOperationException>(state.BeginWrite);
    }

    [Fact]
    public void OneMissingSampleDoesNotUnlockAnotherWrite()
    {
        var state = Ready();
        state.BeginWrite();
        state.Observe(null);
        state.Observe(new SfrCardNumber(2, "00000000"));
        Assert.False(state.CanWrite);
    }

    [Fact]
    public void TwoMissingSamplesThenNewCardAllowWriting()
    {
        var state = Ready();
        state.BeginWrite();
        state.Observe(null);
        state.Observe(null);
        Assert.False(state.CanWrite);
        state.Observe(new SfrCardNumber(2, "00000000"));
        Assert.True(state.CanWrite);
    }

    [Fact]
    public void ReconnectOrPauseDoesNotUnlockUnverifiedWrite()
    {
        var state = Ready();
        state.BeginWrite();
        state.Observe(null);
        state.Invalidate();
        state.Observe(null);
        state.Observe(new SfrCardNumber(3, "00000000"));
        Assert.False(state.CanWrite);
    }

    [Fact]
    public void StoppingClearsStaleCard()
    {
        var state = Ready();
        state.Invalidate();
        Assert.Null(state.Current);
        Assert.False(state.CanWrite);
    }

    private static CardPresence Ready()
    {
        var state = new CardPresence();
        state.Observe(new SfrCardNumber(2, "00000000"));
        return state;
    }
}
