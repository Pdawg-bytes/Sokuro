using Sokuro.Rings;
using Sokuro.Sinks;
using Sokuro.Types;
using Sokuro.Sources;
using Sokuro.Runtime;

namespace Sokuro.Logging;

public static class Logging
{
    private static readonly Lock _lock = new();
    private static LogEngine? _engine;
    private static bool _exitHooked;

    public static ISourceRegistry Registry { get; } = new SourceRegistry(SourceTree.Global);

    public static void Start(LoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        lock (_lock)
        {
            if (_engine is not null)
                throw new InvalidOperationException("Logging has already been started.");

            _engine = new LogEngine(options);
            ConsumerWake.Configure(options.WakeOnLevel);
            _engine.Run();

            SourceTree.Global.SetRoot(options.DefaultLevel);
            SourceTree.Global.SetActive(true);
            if (!_exitHooked)
            {
                _exitHooked = true;

                AppDomain.CurrentDomain.ProcessExit += static (_, _) => _engine?.Shutdown(TimeSpan.FromMilliseconds(500));
            }
        }
    }

    public static bool Flush(TimeSpan timeout)    => Engine.Flush(timeout);
    public static bool Shutdown(TimeSpan timeout) => Engine.Shutdown(timeout);
    public static void DetachCurrentThread()      => Ring.DetachCurrent();
    public static void SetLevel(LogLevel level)   => SourceTree.Global.SetRoot(level);

    public static void SetLevel(LogSource source, LogLevel level)
    {
        if (source.ID == 0) SourceTree.Global.SetRoot(level);
        else                SourceTree.Global.SetNode(source.ID, level);
    }

    public static void SetLevel(LogKind kind, LogLevel level) => SourceTree.Global.SetKind(kind, level);
    public static void SetLevel(LogTag tag, LogLevel level)   => SourceTree.Global.SetTag(tag, level);

    public static void ClearLevel(LogSource source) => SourceTree.Global.ClearNode(source.ID);
    public static void ClearLevel(LogKind kind)     => SourceTree.Global.ClearKind(kind);
    public static void ClearLevel(LogTag tag)       => SourceTree.Global.ClearTag(tag);

    public static void AddSink(ILogSink sink)    => Engine.AddSink(sink);
    public static void RemoveSink(ILogSink sink) => Engine.RemoveSink(sink);

    public static void AttachCurrentThread() => Ring.AttachCurrent();

    public static void RegisterType<T>(LogValueFormatter f) where T : unmanaged => LogTypeInfo<T>.Register(f);

    public static void Prewarm<T>() where T : unmanaged => _ = LogTypeInfo<T>.ID;

    public static LogStatistics GetStatistics() => Engine.Statistics();

    public static Exception? LastError => Engine.LastError;
    public static long ErrorCount      => Engine.ErrorCount;

    public static bool DrainOnce() => Engine.Consumer.DrainOnce();

    internal static LogEngine Engine => _engine ?? throw new InvalidOperationException("Logging has not been started.");
}