using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Sokuro.Runtime;

internal sealed class Clock
{
    internal const long ReanchorSeconds = 60;

    private long _anchorUTCTicks;
    private long _anchorStamp;

    internal Clock() => Align();

    internal void Align()
    {
        _anchorUTCTicks = DateTime.UtcNow.Ticks;
        _anchorStamp    = Stopwatch.GetTimestamp();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal DateTime ToUTC(long timestamp)
    {
        double elapsed = (double)(timestamp - _anchorStamp) / Stopwatch.Frequency;
        return new DateTime(_anchorUTCTicks + (long)(elapsed * TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }
}