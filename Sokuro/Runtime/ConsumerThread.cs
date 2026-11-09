using System.Diagnostics;

namespace Sokuro.Runtime;

internal sealed class ConsumerThread
{
    private const int SpinRounds  = 64;
    private const int YieldRounds = 4;
    private const int IdleParkMs  = 5;

    private readonly LogEngine _engine;
    private readonly Thread _thread;
    private volatile bool _stop;
    private volatile bool _failed;

    internal bool Failed => _failed;

    internal ConsumerThread(LogEngine engine)
    {
        _engine = engine;
        _thread = new Thread(Run) { IsBackground = true, Name = "Sokuro.Consumer" };
    }

    internal void Start() => _thread.Start();

    internal bool Stop(TimeSpan timeout)
    {
        _stop = true;
        return _thread.Join(timeout);
    }

    private void Run()
    {
        try { RunLoop(); }
        catch (Exception exception)
        {
            _engine.ReportFailure(exception);
            _failed = true;
        }
    }

    private void RunLoop()
    {
        int idle      = 0;
        long nextTick = Stopwatch.GetTimestamp() + Stopwatch.Frequency;

        while (!_stop)
        {
            if (_engine.Consumer.DrainOnce()) idle = 0;
            else                              Idle(idle++);

            if (Stopwatch.GetTimestamp() >= nextTick)
            {
                nextTick = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                _engine.Tick();
            }
        }
    }

    private static void Idle(int rounds)
    {
        if (rounds < SpinRounds)
        {
            Thread.SpinWait(20);
        }
        else if (rounds < SpinRounds + YieldRounds)
        {
            Thread.Yield();
        }
        else
        {
            ConsumerWake.Park(IdleParkMs);
        }
    }
}