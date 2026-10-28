using System.Collections.Concurrent;
using System.Globalization;
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

    /// <summary>日志文件保留天数。启动时清理更早的。</summary>
    private const int KeepDays = 7;

    private static readonly object FileLock = new();
    private static readonly ConcurrentQueue<LogEntry> Ring = new();
    private static string? _logFile;
    private static string? _logFileDay;   // _logFile 对应的 yyyyMMdd，用于跨天换文件

    /// <summary>新日志产生时触发（可能在后台线程）。</summary>
    public static event Action<LogEntry>? OnEntry;

    public static IReadOnlyList<LogEntry> Snapshot() => Ring.ToArray();

    public static void Debug(string msg) => Write(LogLevel.Debug, msg);
    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);
    public static void Error(string msg) => Write(LogLevel.Error, msg);

    /// <summary>
    /// 记录异常。带完整堆栈和内层异常——出问题时没有堆栈基本等于没日志。
    /// </summary>
    public static void Error(string msg, Exception ex)
    {
        var sb = new StringBuilder(msg);
        sb.Append(" :: ").Append(ex.GetType().FullName).Append(": ").Append(ex.Message);

        if (!string.IsNullOrEmpty(ex.StackTrace))
            sb.Append(Environment.NewLine).Append(ex.StackTrace);

        // 内层异常（比如 HttpRequestException 包着的 SocketException）往往才是根因，
        // 所以内层的堆栈也要记——只留一句消息的话，最该看的那段调用链就丢了。
        var inner = ex.InnerException;
        var depth = 0;
        while (inner is not null && depth < 3)
        {
            sb.Append(Environment.NewLine)
              .Append("-- 内层: ").Append(inner.GetType().FullName).Append(": ").Append(inner.Message);

            if (!string.IsNullOrEmpty(inner.StackTrace))
                sb.Append(Environment.NewLine).Append(inner.StackTrace);

            inner = inner.InnerException;
            depth++;
        }

        Write(LogLevel.Error, sb.ToString());
    }

    private static void Write(LogLevel level, string msg)
    {
        var now = DateTime.Now;
        var entry = new LogEntry(now, level, msg);
        Ring.Enqueue(entry);
        while (Ring.Count > RingSize) Ring.TryDequeue(out _);

        try { OnEntry?.Invoke(entry); } catch { /* UI 已销毁，忽略 */ }

        try
        {
            lock (FileLock)
            {
                var day = now.ToString("yyyyMMdd");

                // 跨天要换文件。原来的 ??= 只在首次赋值，通宵挂机时第二天的日志
                // 会继续追加进昨天那个文件，文件名和内容对不上。
                if (_logFile is null || _logFileDay != day)
                {
                    _logFileDay = day;
                    _logFile = Path.Combine(AppPaths.LogDir, $"adbsolver-{day}.log");
                }

                File.AppendAllText(_logFile, entry.Display + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写盘失败绝不能影响主流程
        }
    }

    /// <summary>
    /// 清理超过 KeepDays 天的旧日志。开机跑一次就够了，靠 static 构造函数保证。
    /// 日期从文件名解析而不是看文件修改时间——跨天 bug 会让同一个文件里混着两天的内容，
    /// 修改时间不可靠。
    /// </summary>
    static Log()
    {
        try
        {
            var dir = AppPaths.LogDir;
            if (!Directory.Exists(dir)) return;

            var cutoff = DateTime.Today.AddDays(-KeepDays);

            foreach (var f in Directory.EnumerateFiles(dir, "adbsolver-*.log"))
            {
                try
                {
                    var stem = Path.GetFileNameWithoutExtension(f);   // adbsolver-20261028
                    if (stem.Length < 8) continue;

                    var tail = stem[^8..];
                    if (!DateTime.TryParseExact(tail, "yyyyMMdd", CultureInfo.InvariantCulture,
                                                DateTimeStyles.None, out var day))
                        continue;

                    if (day < cutoff) File.Delete(f);
                }
                catch
                {
                    // 单个文件删不掉（被占用等）不影响其他文件
                }
            }
        }
        catch
        {
            // 清理失败绝不能影响启动
        }
    }
}
