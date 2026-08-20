using System.Globalization;
using System.Threading;

internal sealed class SmokeTestMetrics
{
    private int _assertionCount;

    internal int AssertionCount => Volatile.Read(ref _assertionCount);

    internal void Assert(bool condition, string message)
    {
        Interlocked.Increment(ref _assertionCount);
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal string CreateSuccessSummary()
    {
        var assertionCount = AssertionCount;
        if (assertionCount <= 0)
        {
            throw new InvalidOperationException(
                "SmokeTests cannot report success without executing an assertion.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"SMOKE_RESULT tests=1 assertions={assertionCount} passed=1 failed=0");
    }
}
