using System.Diagnostics;
using System.Text;

namespace AdbSolver.Core;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Ok => !TimedOut && ExitCode == 0;
    public string Combined => (StdOut + "\n" + StdErr).Trim();
}

/// <summary>进程调用封装。无窗口、可超时、支持取消、UTF-8 安全解码。</summary>
public static class ProcRunner
{
    public static async Task<ProcResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        TimeSpan timeout,
        CancellationToken ct = default,
        string? workingDir = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var a in args) psi.ArgumentList.Add(a);
        if (workingDir is not null) psi.WorkingDirectory = workingDir;
        if (env is not null)
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        try
        {
            if (!proc.Start())
                return new ProcResult(-1, "", "进程无法启动", false);
        }
        catch (Exception ex)
        {
            return new ProcResult(-1, "", $"{ex.GetType().Name}: {ex.Message}", false);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            // 关闭 stdin，避免个别 adb 子命令等待输入
            try { proc.StandardInput.Close(); } catch { }
        }
        catch { }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            var timedOut = !ct.IsCancellationRequested;
            return new ProcResult(-1, Safe(stdout), Safe(stderr), timedOut);
        }

        // WaitForExitAsync 后需要短暂等待异步读取回调排空
        try { proc.WaitForExit(200); } catch { }

        return new ProcResult(proc.ExitCode, Safe(stdout), Safe(stderr), false);

        static string Safe(StringBuilder sb) { lock (sb) return sb.ToString().TrimEnd(); }
    }
}
