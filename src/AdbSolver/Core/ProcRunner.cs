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

    /// <summary>
    /// 执行命令并把 stdout 当成**原始二进制**收下来。
    ///
    /// 存在的意义：<c>adb exec-out screencap -p</c> 吐的是 PNG 二进制流。
    /// 走上面那个 RunAsync 的话，StandardOutputEncoding=UTF8 + OutputDataReceived
    /// 按行读，二进制会被解码成乱码再重新编码，PNG 头直接废掉——
    /// 所以必须另开一条不碰编码的通道，直接读 BaseStream。
    ///
    /// 返回 null 表示启动失败或超时。stderr 单独给，失败时用它排错。
    /// </summary>
    public static async Task<(byte[]? Data, string StdErr, bool TimedOut)> RunBytesAsync(
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
            // 关键：这里绝对不能设 StandardOutputEncoding，
            // 设了就会强制走 StreamReader 解码，二进制全毁。
        };

        foreach (var a in args) psi.ArgumentList.Add(a);
        if (workingDir is not null) psi.WorkingDirectory = workingDir;
        if (env is not null)
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var proc = new Process { StartInfo = psi };

        try
        {
            if (!proc.Start())
                return (null, "进程无法启动", false);
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}", false);
        }

        try { proc.StandardInput.Close(); } catch { }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        // 必须"先开始读、再等退出"。反过来的话，输出超过管道缓冲（约 4KB）时
        // 子进程会卡在写管道上永远退不出去，而我们又在等它退出 —— 直接死锁。
        var stdoutTask = Task.Run(async () =>
        {
            var ms = new MemoryStream();
            try
            {
                await proc.StandardOutput.BaseStream.CopyToAsync(ms, 81920, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            return ms.ToArray();
        }, CancellationToken.None);

        var stderrTask = Task.Run(async () =>
        {
            try { return await proc.StandardError.ReadToEndAsync().ConfigureAwait(false); }
            catch { return ""; }
        }, CancellationToken.None);

        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        }

        var data = await stdoutTask.ConfigureAwait(false);
        var err = await stderrTask.ConfigureAwait(false);

        if (timedOut) return (data.Length > 0 ? data : null, err.Trim(), true);
        if (data.Length == 0) return (null, err.Trim(), false);
        return (data, err.Trim(), false);
    }
}
