using PeakCan.HIL.Core.HIL;

namespace PeakCan.Host.Cli;

/// <summary>
/// Colored console progress reporter for HIL test execution.
/// </summary>
internal sealed class ConsoleProgress : IProgress<TestProgress>
{
    public void Report(TestProgress value)
    {
        var color = value.PercentComplete switch
        {
            >= 100 => ConsoleColor.Green,
            > 0 => ConsoleColor.Yellow,
            _ => ConsoleColor.Gray,
        };

        Console.ForegroundColor = color;
        Console.Write($"[{value.CompletedCases}/{value.TotalCases}] ");
        Console.ResetColor();
        Console.WriteLine(value.CurrentCaseName ?? value.Message ?? "running");
    }
}
