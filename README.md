# Sokuro
A fast, multi-threaded logging library for .NET designed for low latency and throughput.

**Note:** Its design doesn't guarantee delivery of logs, so Sokuro isn't suitable for audit logging.

## Quick start
```cs
using Sokuro.Sinks;
using Sokuro.Logging;
using Sokuro.Sources;

LoggingOptions options = new() { DefaultLevel = LogLevel.Info };
options.Sinks.Add(new ConsoleSink(useColor: true));
Logging.Start(options);

LogKind cpuKind = LogKind.Register("CPU");
LogSource cpu   = LogSource.Root.Child(cpuKind);

uint address = 0x1234;
cpu.Warn($"Unimplemented operation at {address:X8}");

if (!Logging.Shutdown(TimeSpan.FromSeconds(5)))
    Console.Error.WriteLine($"Shutdown failed: {Logging.LastError}");
```


## Threading models
Each thread that logs has its own ring buffer to sotre records. Sokuro can attach one automatically, or you can manage its lifetime yourself.

### Automatic
In most cases, this is fine. The first enabled log call on a thread does the setup:

```cs
Task.Run(() => cpu.Info($"hello from a worker"));
```

When the thread exits, a finalizer marks the buffer as dead, then the consumer retires it after draining remaining records. This does mean that the GC may not immediately finish the cleanup.

### Manual
For more control, you can attach the buffer when the thread starts and detach before it exits:

```cs
Thread thread = new(() =>
{
    Logging.AttachCurrentThread();
    try
    {
        cpu.Info($"hello from a dedicated thread");
    }
    finally
    {
        Logging.DetachCurrentThread();
    }
});
```

This is better when you want to allocate the buffer before entering a latency-sensitive part of your code, or when you want to ensure its release without waiting for the GC.


## Writing logs

```cs
const ushort CPUMessageID = 100;
source.Info($"opcode=0x{opcode:X8}");
source.Warn(CPUMessageID, $"Unknown opcode {opcode}");
```

The available levels, from least to most severe, are `Trace`, `Debug`, `Info`, `Warn`, `Error`, `Fatal`, and `Off`. Every logging method also has an overload that accepts a `ushort` event ID as shown above.

Sokuro checks the log level before evaluating the arguments, so a disabled log call does almost nothing. It also allows for compiletime filtering:
- `Trace` and `Debug` are compiled out unless `LOG_TRACE` is defined in the calling project. They still also need to pass the runtime filter if enabled.
- Define `SOKURO_DISALBE_LOGGING` in the calling project's `DefineConstants` to compile out **all** log calls. Their arguments are once again not evaluated. This only removes the extension calls though, `Logging.Start` and the rest of the setup code still execute.

### Supported arguments
| Argument | Behavior |
|---|---|
| `string`, `ReadOnlySpan<char>` | Copied into the record. `null` is treated as an empty string. |
| `bool`, integers, `float`, `double`, `char`, `nint`, `nuint`, `Guid` | Formatted by built-in formatters. Numbers use invariant culture. |
| Enums | Formatted by name, unless you registered a formatter for the type first. |
| Other unmanaged types | Use a registered formatter, or fall back to raw bytes in hex. |

Alignment (`{x,8}`) and format strings (`{x:X8}`) are supported, however, format specifiers can be up to 31 characters. Longer ones are ignored.

You can't pass reference types other than `string` either. Call `ToString()` yourself if you need to log one.

### Truncation
There are a few limits on how much text a record can hold, exceeding any of them sets `LogEntry.Truncated`:
- `MaxTextChars` limits each individual string/Span argument.
- `MaxRecordBytes` limits the size of the entire record.
- The consumer's line buffer can also run out of space, for example when a value or source path is very long. In that case, the line ends with `...`.
- 64KiB is the maximum size that an unregistered unmanaged type can be, else it's truncated when displayed.

### Custom formatters
Register a formatter before the type is first logged or passed to `Prewarm<T>()`:

```cs
Logging.RegisterType<Point>(static (ReadOnlySpan<byte> raw, ReadOnlySpan<char> format, Span<byte> utf8, out int written) =>
{
    Point value = MemoryMarshal.Read<Point>(raw);
    return value.X.TryFormat(utf8, out written, format);
});
```

Formatters run on the consumer thread, so don't retain any of the passed spans. If a formatter throws, returns `false`, or reports an invalid output length, Sokuro writes `<fmt error>`. Registering the same type twice, registering it after first use, or replacing a built-in formatter throws `InvalidOperationException`.


## Sources, kinds, and tags
This is the system Sokuro uses to describe the sources and paths of logs.

Log sources form a tree starting at `LogSource.Root`.
- A **kind** identifies a category, like `CPU` or `Network`.
- An optional **index** distinguishes instances, such as `CPU #0` and `CPU #1`. Negative indices are omitted.
- **Tags** let you group sources from different parts of the tree for filtering. Up to 64 are allowed to be registered at once.

```cs
LogKind cpu    = LogKind.Register("CPU");
LogTag io      = LogTag.Register("IO");
LogTag verbose = LogTag.Register("Verbose");

LogSource core = LogSource.Root.Child(cpu, index: 0, tags: io | verbose);
```

A source path might look like `[CPU #0]` or `[Emulator/CPU #0]`.

Calling `Child` with a parent, kind, and index that already exist returns the existing source. The tags mast match, otherwise, it throws. The tree can hold 65,536 sources. Once it's full, `Child` returns the parent and increments `LogStatistics.SourceOverflows`.

### Filtering by level
You can set a level for the root, a kind, a tag, or an individual source:

```cs
Logging.SetLevel(LogLevel.Info);         // root
Logging.SetLevel(cpu, LogLevel.Debug);   // every source of this kind
Logging.SetLevel(io, LogLevel.Warn);     // every source with this tag
Logging.SetLevel(core, LogLevel.Error);
Logging.ClearLevel(core);                // kinds and tags can be cleared too

if (core.IsEnabled(LogLevel.Warn)) { /* optional work */ }
```

Sokuro chooses a source's effective level using the first setting it finds in this order:
1. The source's own level.
2. Its kind's level.
3. The most verbose level set on any of its tags.
4. Its parent's effective level.

Changing a level recomputes the levels for every source, so avoid doing it in a hot loop.


## Configuration
Set the options before calling `Logging.Start`:

```csharp
LoggingOptions options = new()
{
    DefaultLevel   = LogLevel.Info,
    RingBytes      = 128 * 1024,
    MaxRingBytes   = 4 * 1024 * 1024,
    MaxRecordBytes = 1024,
    MaxTextChars   = 256,
    ErrorHeadroom  = 0.125,
    ReorderDelay   = TimeSpan.Zero,
    WakeOnLevel    = LogLevel.Off,
};

options.Sinks.Add(new ConsoleSink());
Logging.Start(options);
```

| Option | Default | Description |
|---|---:|---|
| `DefaultLevel` | `Info` | Log level for the root source. |
| `RingBytes` | 128 KiB | Initial size of each thread's ring. |
| `MaxRingBytes` | 4 MiB | Maximum size a ring can grow to. |
| `MaxRecordBytes` | 1024 | Maximum size of a record written by a producer. |
| `MaxTextChars` | 256 | Maximum length of an individual string or span argument. |
| `ErrorHeadroom` | 0.125 | Fraction of each ring reserved for `Error` and `Fatal`. Lower-severity records are dropped before using this space. |
| `ReorderDelay` | 0 | How long the consumer holds records back to give records from other threads time to arrive. Can improve ordering, but adds latency. |
| `WakeOnLevel` | `Off` | Records at or above this level wake an idle consumer immediately. `Off` disables this. Regardless of this, an idle consumer wakes on its own every 5 ms and whenever a ring is more than half full. |
| `Sinks` | empty | Sinks to register at startup. With no sinks, records aren't output. |
| `ManualPump` | `false` | Disables the consumer thread. You must call `Logging.DrainOnce()` yourself. |

`Logging.Start` throws `ArgumentException` if the options fail validation:
- `RingBytes` and `MaxRingBytes` must be powers of two.
- `MaxRingBytes` must be at least `RingBytes`.
- `RingBytes` must be at least `64 * MaxRecordBytes`.
- `MaxRecordBytes` must be a multiple of 8 and at least 128.
- `MaxTextChars` must be between 1 and 65,535.
- `ErrorHeadroom` must be in the range `[0, 1)`.
- `ReorderDelay` can't be negative.

### Ring growth
Once per second, the consumer checks each producer's buffer. If a buffer has dropped records on two consecutive checks and hasn't reached `MaxRingBytes`, Sokuro doubles its size. Ring growth checks don't run in manual-pump mode.

### Manual pumping
Set `ManualPump = true` if you don't want Sokuro to create a consumer thread. You then need to call `Logging.DrainOnce()` yourself. `Flush` and `Shutdown` also drain pending records.

Sinks that implement `ILogSink` directly run on whichever thread calls `DrainOnce`. In manual-pump mode, Sokuro doesn't grow rings or re-anchor the clock.


## Sinks
A sink receives every record that makes it through the consumer. You can add or remove sinks while logging is running with `Logging.AddSink` and `Logging.RemoveSink`.

Removing a sink doesn't dispose it, and it doesn't wait for writes that are already in progress.

### ConsoleSink

```csharp
new ConsoleSink(useColor: false, capacityBytes: 1 << 20)
new ConsoleSink(Stream output, useColor: false, capacityBytes: 1 << 20)
```

`ConsoleSink` writes UTF-8 lines to standard output, or to the stream you provide. It has its own worker thread. Set `useColor` to add ANSI color escapes based on log level.

### FileSink

```csharp
new FileSink("logs/app.log", maxFileBytes: 16 * 1024 * 1024, retainedFiles: 3,
             flushInterval: TimeSpan.FromSeconds(1), capacityBytes: 1 << 20)
```

`FileSink` appends UTF-8 lines to a file on its own worker thread. It creates the containing directory if it doesn't exist.

- **Rotation:** Before a line would take the file past `maxFileBytes`, the current file is renamed to `app.log.1`, older files are shifted up, and the file beyond `retainedFiles` is deleted. With `retainedFiles = 0`, the file is truncated instead.
- **Flushing:** When the sink is idle, it flushes to the OS every `flushInterval`. `Logging.Flush` also forces a flush.

### Buffered sinks
`ConsoleSink` and `FileSink` both derive from `BufferedSink`. Each has a fixed-size queue between the consumer and its worker thread. A slow terminal or disk therefore won't block the consumer directly.

A few things to keep in mind:
- If the queue fills up, the sink drops the record. The `Dropped` counter tracks these drops.
- `Error` and `Fatal` records get the same reserved-headroom treatment as they do in the rings.
- A record larger than a quarter of the queue's capacity is always dropped.
- If a write throws, the sink increments `FailedBatches` and stores the exception in `LastError`. A batch may have been partly written, so `FailedBatches` is not a count of lost records.
- `Flush()` throws if it times out (`FlushTimeout`, 5 seconds by default) or if the worker has failed.

### TailSink
`TailSink` keeps an in-memory history of recent log lines. It doesn't write to an external destination and runs on the consumer thread.

```csharp
TailSink tail = new(capacityBytes: 1 << 20);
Logging.AddSink(tail);

tail.Query(new TailQuery(MinLevel: LogLevel.Warn, Limit: 20), static (in TailEntry e) =>
{
    Console.WriteLine(Encoding.UTF8.GetString(e.Line));
    return true; // false stops early
});
```

New lines replace the oldest ones when the tail buffer gets full. Lines longer than `maxLineBytes` (8,192 by default) are clipped, but `TailEntry` doesn't indicate whether or not that happens.

`Query` visits matching entries from newest to oldest. You can filter with these `TailQuery` fields:
| Field | Matches |
|---|---|
| `MinLevel` | This level or higher. Defaults to `Trace`. |
| `Subtree` | A source and all of its descendants. |
| `Kind` | Sources with the given kind. |
| `Tag` | Sources with the given tag. |
| `Limit` | Maximum number of matches. `0` means no limit. |

`Query` returns the number of entries visited. Each `TailEntry` contains `Level`, `SourceID`, `Timestamp`, `Synthetic`, and `Line`.

The `Line` span is only valid for the duration of the visitor call. `Query` holds the sink's lock for the entire call, which prevents the consumer from writing to the tail in the meantime. Keep visitors short, and don't log from inside one.

The tail stores less information than a full `LogEntry`. It doesn't include the event ID, thread ID, or truncation flag.

Records only appear in the tail after the consumer has processed them. Call `Logging.Flush` first if you need to query everything logged up to that point.

### Custom sinks
There are two ways to write a custom sink.

**Synchronous sinks:** Implement `ILogSink` (`Write(in LogEntry)` and `Flush()`). These callbacks run on the consumer thread. A slow `Write` delays every other sink and the logging pipeline as a whole. Sokuro catches exceptions and records them in `Logging.LastError`.

Don't call `Flush`, `Shutdown`, or similar lifecycle methods from inside a sink callback.

**Buffered sinks:** Derive from `BufferedSink` and implement `WriteBatch(BufferedBatch)`. You can also override `FlushOutput`, `OnIdle`, and `Close`.

Read entries with `batch.TryRead(out BufferedEntry entry)`. Call the protected `Start()` at the end of your most-derived constructor, after initializing all fields. The worker can start running as soon as `Start()` is called.

`LogEntry` exposes:

- `Timestamp`: stopwatch ticks.
- `UTCTime`: UTC time.
- `Level`, `SourceID`, `ThreadID`, and `EventID`.
- `Truncated` and `Synthetic`.
- `Message`: message text without a prefix or newline.
- `Line`: the complete UTF-8 line, including timestamp, level, source, and newline.

Spans are only valid for the duration of the callback. `BufferedEntry` exposes the same data except for `UTCTime`, `ThreadID`, and `Message`.


## Log output
A typical line looks like this:

```
[14:03:22.123456 WRN] [CPU #0] Unimplemented operation at 00001234
```

Timestamps are UTC with microsecond precision. Sokuro derives them from the record's stopwatch timestamp and a time anchor that it refreshes every 60 seconds. The displayed time can drift slightly from the system clock.


## Flushing and shutdown
| Call | Purpose |
|---|---|
| `Logging.Flush(timeout)` | Takes a snapshot of published records, waits for the consumer to process them, and then flushes every sink. Records logged after the snapshot aren't included. |
| `Logging.Shutdown(timeout)` | Stops new log calls, waits for in-progress calls to finish, drains the remaining records, stops the consumer, and flushes and disposes the sinks. |

Both methods return `false` if they time out or if Sokuro has recorded an error. You can retry a shutdown that timed out.

Once shutdown has started, adding or removing sinks and attaching threads will throw. Sokuro also attempts a 500 ms shutdown when the process exits, but you should call `Shutdown` yourself.

One important edge case: don't let an interpolated argument throw an exception. By that point, the logging handler has already entered the shutdown, but it won't commit the record. `Shutdown` can then time out waiting for it.

A timeout can't interrupt a sink callback that's blocked.

### Ordering
The consumer sorts each batch (up to 4,096 records) by timestamp. Records from different threads will usually appear in order, but Sokuro doesn't guarantee a global ordering as a record from a slow thread may arrive in a later batch.

`ReorderDelay` can give records more time to arrive before a batch is processed, but it doesn't guarantee global ordering.

### Dropped records and statistics
Records can be dropped in two places:
1. A producer's ring is full.
2. A buffered sink's queue is full.

When the consumer notices that a ring has dropped records, it emits a synthetic warning such as `Dropped N records on thread T`. This is an ordinary record, so a full sink can drop the warning too.

Get the current counters with `Logging.GetStatistics()`:

```csharp
LogStatistics stats = Logging.GetStatistics();
```

| Counter | Meaning |
|---|---|
| `Records` | Records processed by the consumer. This isn't the same as the number written by sinks. |
| `Dropped` | Ring drops plus buffered-sink drops during dispatch. If two sinks drop the same record, it counts twice. |
| `SourceOverflows` | Number of times the source tree was full. |
| `Threads` | Rings currently registered, including dead rings that haven't been retired yet. |
| `Logging.LastError`, `Logging.ErrorCount` | The most recent engine error and the total number of errors seen. |

Errors are sticky. Once Sokuro records an error, `Flush` and `Shutdown` continue to return `false`.

A buffered sink's `LastError` is available on the sink immediately. Sokuro copies it to `Logging.LastError` the next time a flush or shutdown checks the sink.


## Other APIs

- `Logging.Registry`: `ParentOf`, `KindOf`, `IndexOf`, `HasTag`, `IsDescendantOf`, and `AppendPath` for inspecting sources.
- `Logging.Prewarm<T>()`: Resolves a type's formatter before its first log call.
- `LogSource.IsEnabled(LogLevel)`: Checks whether a level is enabled for a source. Useful for guarding expensive work.

Create `LogSource`, `LogKind`, and `LogTag` values through `Child` and `Register`. Other values are validated, but won't refer to registered sources, kinds, or tags.