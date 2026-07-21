using Microsoft.Extensions.Logging;

namespace Obstruo.Shared.Logging;

/// <summary>
/// Minimal rolling file logger — one file per day, oldest files pruned.
/// Exists so field problems (failed install, DNS outage, tamper events) can be
/// diagnosed after the fact; EventLog and AddDebug leave nothing on disk that
/// support can ask a user to send.
///
/// Deliberately tiny instead of a Serilog dependency:
///   - single writer lock (log volume here is low — no batching needed),
///   - never throws into the caller: a logging failure must never take down
///     the DNS path,
///   - files named {prefix}-yyyyMMdd.log in the given directory.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly string _prefix;
    private readonly LogLevel _minLevel;
    private readonly int _maxFiles;
    private readonly object _writeLock = new();

    private string? _currentPath;
    private StreamWriter? _writer;
    private bool _broken;   // set when the directory is unwritable — stop retrying

    public FileLoggerProvider(
        string directory, string prefix,
        LogLevel minLevel = LogLevel.Information, int maxFiles = 7)
    {
        _directory = directory;
        _prefix = prefix;
        _minLevel = minLevel;
        _maxFiles = maxFiles;

        try
        {
            Directory.CreateDirectory(_directory);
            PruneOldFiles();
        }
        catch
        {
            _broken = true;
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_writeLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        if (_broken) return;

        var now = DateTime.Now;
        var line =
            $"{now:yyyy-MM-dd HH:mm:ss.fff} [{Abbrev(level)}] {category}: {message}" +
            (exception is null ? "" : Environment.NewLine + exception);

        lock (_writeLock)
        {
            try
            {
                var path = Path.Combine(_directory, $"{_prefix}-{now:yyyyMMdd}.log");
                if (path != _currentPath)
                {
                    _writer?.Dispose();
                    _writer = new StreamWriter(
                        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    { AutoFlush = true };
                    _currentPath = path;
                    PruneOldFiles();
                }

                _writer!.WriteLine(line);
            }
            catch
            {
                // Never propagate a logging failure. Mark broken so a dead disk
                // or revoked ACL doesn't cost an exception per log call forever.
                _broken = true;
                try { _writer?.Dispose(); } catch { /* ignore */ }
                _writer = null;
            }
        }
    }

    private void PruneOldFiles()
    {
        var files = Directory.GetFiles(_directory, $"{_prefix}-*.log");
        if (files.Length <= _maxFiles) return;

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var stale in files[..^_maxFiles])
        {
            try { File.Delete(stale); } catch { /* in use or gone — skip */ }
        }
    }

    private static string Abbrev(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel >= _provider._minLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _provider.Write(logLevel, _category, formatter(state, exception), exception);
        }
    }
}
