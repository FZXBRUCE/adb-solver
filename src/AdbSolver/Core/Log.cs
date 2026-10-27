using System.Collections.Concurrent;
using System.Text;

namespace AdbSolver.Core;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string Display => $"{Time:HH:mm:ss} [{Level.ToString().ToUpperInvariant()[0]}] {Message}";
}

/// <summary>线程安全的滚动文件日志 + 内存环形缓冲（供 UI 订阅）。</summary>
public static class Log
{
    private const int RingSize = 3000;
    private static readonly object FileLock = new();
    private static readonly ConcurrentQueue<LogEntry> Ring = new();
    private static string? _logFile;

    /// <summary>新日志产生时触发（可能在后台线程）。</summary>
    public static event Action<LogEntry>? OnEntry;

    public static IReadOnlyList<LogEntry> Snapshot() => Ring.ToArray();

    public static void Debug(string msg) => Write(LogLevel.Debug, msg);
    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);
    public static void Error(string msg) => Write(LogLevel.Error, msg);

    public static void Error(string msg, Exception ex) =>
        Write(LogLevel.Error, $"{msg} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(LogLevel level, string msg)
    {
        var entry = new LogEntry(DateTime.Now, level, msg);
        Ring.Enqueue(entry);
        while (Ring.Count > RingSize) Ring.TryDequeue(out _);

        try { OnEntry?.Invoke(entry); } catch { /* UI 已销毁，忽略 */ }

        try
        {
            lock (FileLock)
            {
                _logFile ??= Path.Combine(AppPaths.LogDir, $"adbsolver-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(_logFile, entry.Display + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写盘失败绝不能影响主流程
        }
    }
}
