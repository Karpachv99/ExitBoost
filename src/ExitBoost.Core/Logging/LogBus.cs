using System.Collections.Concurrent;

namespace ExitBoost.Core.Logging;

public enum LogLevel { Debug, Info, Warn, Error, Success }

public sealed record LogEntry(DateTime At, LogLevel Level, string Message)
{
    public string Time => At.ToLocalTime().ToString("HH:mm:ss");

    public string LevelText => Level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Success => "OK ",
        _ => "???"
    };
}

/// <summary>Централизованная шина логов с кольцевым буфером.</summary>
public sealed class LogBus
{
    private const int Capacity = 4000;
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly List<StreamWriter> _fileWriters = new();
    private readonly object _fileSync = new();
    private bool _fileLogging;

    public event Action<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> Snapshot => _entries.ToList();

    public void EnableFileLogging(string path)
    {
        lock (_fileSync)
        {
            if (_fileLogging) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var writer = new StreamWriter(path, append: true) { AutoFlush = true };
                _fileWriters.Add(writer);
                _fileLogging = true;
            }
            catch
            {
                _fileLogging = false;
            }
        }
    }

    public void DisableFileLogging()
    {
        lock (_fileSync)
        {
            foreach (var w in _fileWriters) { try { w.Dispose(); } catch { } }
            _fileWriters.Clear();
            _fileLogging = false;
        }
    }

    public void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.UtcNow, level, message);
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity && _entries.TryDequeue(out _)) { }

        lock (_fileSync)
        {
            if (!_fileLogging) return;
            foreach (var w in _fileWriters)
            {
                try { w.WriteLine($"{entry.Time} [{entry.LevelText}] {entry.Message}"); }
                catch { }
            }
        }

        EntryAdded?.Invoke(entry);
    }

    public void Debug(string m) => Write(LogLevel.Debug, m);
    public void Info(string m) => Write(LogLevel.Info, m);
    public void Warn(string m) => Write(LogLevel.Warn, m);
    public void Error(string m) => Write(LogLevel.Error, m);
    public void Success(string m) => Write(LogLevel.Success, m);

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
    }
}
