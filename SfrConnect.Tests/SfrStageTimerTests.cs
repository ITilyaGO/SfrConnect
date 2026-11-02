namespace SfrConnect.Tests;

public class SfrStageTimerTests
{
    [Fact]
    public void FinishedStagesAreLoggedOnceAndDisposeDoesNotReportFailure()
    {
        var messages = new List<string>();
        using (var timer = new SfrStageTimer(messages.Add, "read"))
        {
            timer.Next("write");
            timer.Finish();
            timer.Finish();
        }
        Assert.Equal(4, messages.Count);
        Assert.Contains("read · начало", messages[0]);
        Assert.Contains("read · готово", messages[1]);
        Assert.Contains("write · начало", messages[2]);
        Assert.Contains("write · готово", messages[3]);
        Assert.Contains("мс (", messages[3]);
        Assert.DoesNotContain(messages, m => m.Contains("прервано"));
    }

    [Fact]
    public void ExceptionRecordsInterruptedStageWithElapsedTime()
    {
        var messages = new List<string>();
        Assert.Throws<IOException>((Action)(() =>
        {
            using var timer = new SfrStageTimer(messages.Add, "verify");
            throw new IOException();
        }));
        Assert.Equal(2, messages.Count);
        Assert.Contains("verify · прервано", messages[1]);
        Assert.Contains("мс (", messages[1]);
    }
}
