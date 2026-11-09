using Sokuro.Sources;

namespace Sokuro.Runtime;

internal static class ConsumerWake
{
    private static readonly SemaphoreSlim _lock = new(0, int.MaxValue);
    private static volatile bool _parked;

    internal static LogLevel Threshold = LogLevel.Off;

    internal static bool Enabled => Threshold != LogLevel.Off;

    internal static void Configure(LogLevel level) => Threshold = level;

    internal static void Signal()
    {
        if (_parked)
        {
            _parked = false;
            _lock.Release();
        }
    }

    internal static void Park(int milliseconds)
    {
        _parked = true;
        _lock.Wait(milliseconds);
        _parked = false;
    }
}