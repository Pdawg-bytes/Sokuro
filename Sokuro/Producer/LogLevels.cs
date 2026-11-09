using Sokuro.Sources;

namespace Sokuro.Producer;

public interface ILogLevel { static abstract LogLevel Value { get; } }

public readonly struct TraceLevel : ILogLevel { public static LogLevel Value => LogLevel.Trace; }
public readonly struct DebugLevel : ILogLevel { public static LogLevel Value => LogLevel.Debug; }
public readonly struct InfoLevel  : ILogLevel { public static LogLevel Value => LogLevel.Info;  }
public readonly struct WarnLevel  : ILogLevel { public static LogLevel Value => LogLevel.Warn;  }
public readonly struct ErrorLevel : ILogLevel { public static LogLevel Value => LogLevel.Error; }
public readonly struct FatalLevel : ILogLevel { public static LogLevel Value => LogLevel.Fatal; }