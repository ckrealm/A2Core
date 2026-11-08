using System.Collections.Concurrent;
using System.Diagnostics;

namespace Aion.Host.Diagnostics;

/// <summary>
/// In-memory counters since the host started, for the staff diagnostics API (GET /api/admin/diagnostics, docs/DIAGNOSTICS.md):
/// open login-port connections, sign-in outcomes per entry point, Error/Critical log entries per log category (this is also
/// a logger provider) and the game requests that got no reply. Nothing is stored or sent anywhere; a restart clears it.
/// No message text is kept (log messages can name accounts), only counts, exception type names and times.
/// </summary>
public sealed class HostDiagnostics : ILoggerProvider
{
    /// <summary>Sign-in entry points: the account site, the native login port and the native game port.</summary>
    public const string WebLogin = "web", NativeLogin = "nativeLogin", NativeGame = "nativeGame";

    public enum Outcome { Succeeded, Failed, Refused }

    sealed class LoginCounts { public long Succeeded, Failed, Refused; }
    sealed class ErrorCounts { public long Errors, Critical, WarningsWithException; public string? LastType; public DateTimeOffset? LastAt; }
    sealed class OpcodeCounts { public long Count, Connections; }

    readonly ConcurrentDictionary<string, LoginCounts> logins = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, ErrorCounts> errors = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<int, OpcodeCounts> unanswered = new();
    readonly object cpuSync = new();
    (long Ticks, double CpuSeconds)? cpuBefore;
    int loginConnections;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public int LoginConnections => Volatile.Read(ref loginConnections);

    public void LoginConnected() => Interlocked.Increment(ref loginConnections);
    public void LoginDisconnected() => Interlocked.Decrement(ref loginConnections);

    /// <summary>Counts one sign-in attempt (Refused: valid credentials turned away by a ban or maintenance).</summary>
    public void Login(string entry, Outcome outcome)
    {
        var counts = logins.GetOrAdd(entry, _ => new LoginCounts());
        switch (outcome)
        {
            case Outcome.Succeeded: Interlocked.Increment(ref counts.Succeeded); break;
            case Outcome.Failed: Interlocked.Increment(ref counts.Failed); break;
            default: Interlocked.Increment(ref counts.Refused); break;
        }
    }

    /// <summary>A game request without a reply; firstOnConnection when this connection had not sent this opcode unanswered before.</summary>
    public void Unanswered(int opcode, bool firstOnConnection)
    {
        var counts = unanswered.GetOrAdd(opcode, _ => new OpcodeCounts());
        Interlocked.Increment(ref counts.Count);
        if (firstOnConnection) Interlocked.Increment(ref counts.Connections);
    }

    /// <summary>Process CPU over the window since the previous call (at least 1 s; else since the start), as a share of all cores.</summary>
    public (double Percent, double Cores, double WindowSeconds) Cpu()
    {
        var cpu = Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;
        var now = Stopwatch.GetTimestamp();
        lock (cpuSync)
        {
            double used, seconds;
            if (cpuBefore is { } before && Stopwatch.GetElapsedTime(before.Ticks, now).TotalSeconds >= 1)
            {
                seconds = Stopwatch.GetElapsedTime(before.Ticks, now).TotalSeconds;
                used = cpu - before.CpuSeconds;
            }
            else
            {
                seconds = Math.Max(0.001, (DateTimeOffset.UtcNow - StartedAt).TotalSeconds);
                used = cpu;
            }
            if (cpuBefore is null || Stopwatch.GetElapsedTime(cpuBefore.Value.Ticks, now).TotalSeconds >= 1) cpuBefore = (now, cpu);
            var cores = used / seconds;
            return (Math.Round(cores / Environment.ProcessorCount * 100, 1), Math.Round(cores, 3), Math.Round(seconds, 1));
        }
    }

    public object Logins() => new[] { WebLogin, NativeLogin, NativeGame }.ToDictionary(entry => entry, entry =>
    {
        var c = logins.GetOrAdd(entry, _ => new LoginCounts());
        return new { succeeded = Interlocked.Read(ref c.Succeeded), failed = Interlocked.Read(ref c.Failed), refused = Interlocked.Read(ref c.Refused) };
    });

    public object Errors() => errors.OrderByDescending(e => e.Value.Errors + e.Value.Critical).ThenBy(e => e.Key, StringComparer.Ordinal)
        .Select(e =>
        {
            lock (e.Value) return new { source = e.Key, errors = e.Value.Errors, critical = e.Value.Critical,
                warningsWithException = e.Value.WarningsWithException, lastType = e.Value.LastType, lastAt = e.Value.LastAt };
        }).ToList();

    public object UnansweredOpcodes() => unanswered.OrderByDescending(o => Interlocked.Read(ref o.Value.Count)).ThenBy(o => o.Key)
        .Select(o => new { opcode = $"0x{o.Key:x4}", count = Interlocked.Read(ref o.Value.Count), connections = Interlocked.Read(ref o.Value.Connections) })
        .ToList();

    void Record(string category, LogLevel level, Exception? exception)
    {
        var counts = errors.GetOrAdd(category, _ => new ErrorCounts());
        lock (counts)
        {
            if (level == LogLevel.Critical) counts.Critical++;
            else if (level == LogLevel.Error) counts.Errors++;
            else counts.WarningsWithException++;
            counts.LastType = exception?.GetType().Name ?? counts.LastType;
            counts.LastAt = DateTimeOffset.UtcNow;
        }
    }

    ILogger ILoggerProvider.CreateLogger(string categoryName) => new CountingLogger(this, categoryName);
    void IDisposable.Dispose() { }

    /// <summary>Counts Error and Critical entries, and Warnings that carry an exception (the native listeners log those).</summary>
    sealed class CountingLogger(HostDiagnostics owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel is LogLevel.Error or LogLevel.Critical || logLevel == LogLevel.Warning && exception is not null)
                owner.Record(category, logLevel, exception);
        }
    }
}
