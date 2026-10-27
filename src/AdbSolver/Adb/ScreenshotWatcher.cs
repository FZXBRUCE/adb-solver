using System.Security.Cryptography;
using AdbSolver.Adb;
using AdbSolver.Core;

namespace AdbSolver.Adb;

/// <summary>
/// 截图监听器。
///
/// 设计要点（这也是"不被监测软件发现"的关键）：
///   1. 不在手机上装任何 App、不注入进程、不 hook 系统 API —— 全部动作都在 PC 侧通过 adb 完成。
///      adb shell 的权限等级与「开发者选项 + USB 调试」本身一致，属于系统自带能力，
///      不引入额外的可疑进程、不申请悬浮窗、不申请无障碍、不读其他 App 私有目录。
///   2. 监听截图用两种手段并行：
///      - Poll  : 轮询 MediaStore 里 image 表的最新一条记录（screencap 落盘后系统会索引到 MediaStore）
///      - Observer: 常驻 `adb shell content watch` 监听 MediaStore 变更（Android 8+ 可用）
///      两者都只是"读系统媒体库的元数据"，不触碰目标 App。
///   3. 真正取图用 `screencap -p` 输出到 /sdcard 再 pull 回来，用完立刻删远端临时文件。
///   4. 支持 Hybrid：Observer 负责低延迟触发，Poll 负责兜底（Observer 挂了也能工作）。
/// </summary>
public sealed class ScreenshotWatcher : IDisposable
{
    private readonly AdbClient _adb;
    private readonly CaptureConfig _cfg;
    private readonly string _serial;

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private Task? _observerTask;
    private readonly object _lock = new();

    private long _lastSeenId = -1;
    private long _lastSeenDateAdded = -1;
    private string _lastHash = "";
    private DateTime _lastEmit = DateTime.MinValue;
    private readonly TaskCompletionSource _primed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<string>? OnScreenshot;   // 参数：本地图片路径
    public event Action<string>? OnStatus;

    public bool Running { get; private set; }

    public ScreenshotWatcher(AdbClient adb, CaptureConfig cfg, string serial)
    {
        _adb = adb;
        _cfg = cfg;
        _serial = serial;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (Running) return;
            _cts = new CancellationTokenSource();
            Running = true;
        }

        var ct = _cts!.Token;
        Directory.CreateDirectory(CaptureDir);

        // 初始化基线：把"启动前就存在的最后一张图"作为已读，避免一启动就误触发。
        // 两个循环都会先 await _primed，保证基线一定在监听之前建立。
        _ = Task.Run(async () =>
        {
            try { await PrimeBaselineAsync(ct).ConfigureAwait(false); }
            finally { _primed.TrySetResult(); }
        }, ct);

        if (_cfg.Mode is CaptureMode.Poll or CaptureMode.Hybrid)
            _pollTask = Task.Run(() => PollLoopAsync(ct), ct);

        if (_cfg.Mode is CaptureMode.Observer or CaptureMode.Hybrid)
            _observerTask = Task.Run(() => ObserverLoopAsync(ct), ct);

        OnStatus?.Invoke($"截图监听已启动（模式: {_cfg.Mode}）");
        Log.Info($"ScreenshotWatcher 启动 mode={_cfg.Mode} serial={_serial}");
    }

    private string CaptureDir
    {
        get
        {
            var dir = Path.Combine(AppPaths.DataDir, "captures");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!Running) return;
            Running = false;
            try { _cts?.Cancel(); } catch { }
            _cts?.Dispose();
            _cts = null;
        }
        OnStatus?.Invoke("截图监听已停止");
        Log.Info("ScreenshotWatcher 停止");
    }

    /// <summary>记录当前 MediaStore 最新一条，作为去重基线。</summary>
    private async Task PrimeBaselineAsync(CancellationToken ct)
    {
        try
        {
            var latest = await QueryLatestImageAsync(ct).ConfigureAwait(false);
            if (latest.HasValue)
            {
                _lastSeenId = latest.Value.Id;
                _lastSeenDateAdded = latest.Value.DateAdded;
                Log.Info($"截图基线已建立: _id={_lastSeenId} date_added={_lastSeenDateAdded}");
            }
            else
            {
                Log.Warn("未能读取 MediaStore 基线（可能无图片或权限不足），将以启动时刻为准");
                _lastSeenDateAdded = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"建立截图基线失败: {ex.Message}");
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // 等基线建立，否则可能把启动前的老图当成新图
        try { await _primed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (TimeoutException) { }

        var interval = Math.Max(200, _cfg.PollIntervalMs);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var latest = await QueryLatestImageAsync(ct).ConfigureAwait(false);
                if (latest.HasValue && IsNew(latest.Value))
                {
                    _lastSeenId = latest.Value.Id;
                    _lastSeenDateAdded = latest.Value.DateAdded;
                    await EmitCaptureAsync("Poll", ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Debug($"轮询异常: {ex.Message}");
            }

            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// 用 content watch 常驻监听 MediaStore 变更。
    /// 只有 Android 8.0+ 的 content 命令才带 watch 子命令；不支持时静默降级到纯轮询。
    /// </summary>
    private async Task ObserverLoopAsync(CancellationToken ct)
    {
        // 等基线建立
        try { await _primed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (TimeoutException) { }

        // 先探测是否支持 content watch
        var probe = await _adb.RunAsync(new[] { "shell", "content", "help" }, _serial, ct, 8000)
            .ConfigureAwait(false);
        var supportsWatch = probe.Combined.Contains("watch", StringComparison.OrdinalIgnoreCase);
        if (!supportsWatch)
        {
            Log.Warn("该设备的 content 命令不支持 watch，Observer 模式降级为轮询");
            OnStatus?.Invoke("设备不支持 content watch，已降级为轮询监听");
            return;
        }

        Log.Info("启动 content watch 监听 MediaStore");
        OnStatus?.Invoke("已启用 ContentObserver 低延迟监听");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var args = new[]
                {
                    "shell", "content", "watch",
                    "--uri", "content://media/external/images/media"
                };

                // content watch 是常驻进程，需要较长超时；超时即视为该轮结束
                var r = await _adb.RunAsync(args, _serial, ct, 30000).ConfigureAwait(false);

                if (r.TimedOut)
                {
                    // 正常：30 秒没变化，继续监听
                    continue;
                }

                // 有变更输出 -> 立刻查一次最新图片
                var latest = await QueryLatestImageAsync(ct).ConfigureAwait(false);
                if (latest.HasValue && IsNew(latest.Value))
                {
                    _lastSeenId = latest.Value.Id;
                    _lastSeenDateAdded = latest.Value.DateAdded;
                    await EmitCaptureAsync("Observer", ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Debug($"Observer 异常: {ex.Message}");
                try { await Task.Delay(1500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private bool IsNew((long Id, long DateAdded) latest)
    {
        // _id 递增是最可靠的新增信号；date_added 作为补充（有些机型 _id 会复用）
        if (latest.Id > _lastSeenId) return true;
        if (latest.Id == _lastSeenId) return false;
        return latest.DateAdded > _lastSeenDateAdded;
    }

    /// <summary>查询 MediaStore 中最新的图片记录（_id + date_added）。</summary>
    private async Task<(long Id, long DateAdded)?> QueryLatestImageAsync(CancellationToken ct)
    {
        var args = new[]
        {
            "shell", "content", "query",
            "--uri", "content://media/external/images/media",
            "--projection", "_id:date_added",
            "--sort", "date_added DESC LIMIT 1"
        };

        var r = await _adb.RunAsync(args, _serial, ct, 8000).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(r.StdOut))
            return null;

        // 典型输出：
        // Row: 0 _id=12345, date_added=1699999999
        var idMatch = System.Text.RegularExpressions.Regex.Match(r.StdOut, @"_id=(\d+)");
        var dateMatch = System.Text.RegularExpressions.Regex.Match(r.StdOut, @"date_added=(\d+)");
        if (!idMatch.Success) return null;

        var id = long.Parse(idMatch.Groups[1].Value);
        var dateAdded = dateMatch.Success ? long.Parse(dateMatch.Groups[1].Value) : 0;
        return (id, dateAdded);
    }

    /// <summary>抓一张截图并发出去。</summary>
    private async Task EmitCaptureAsync(string source, CancellationToken ct)
    {
        // 让文件系统/编码完成落盘
        if (_cfg.SettleDelayMs > 0)
        {
            try { await Task.Delay(_cfg.SettleDelayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        var fileName = $"cap_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
        var localPath = Path.Combine(CaptureDir, fileName);

        var ok = await _adb.PullScreenshotAsync(_serial, localPath, ct).ConfigureAwait(false);
        if (!ok)
        {
            Log.Warn("截图拉取失败，跳过本次");
            return;
        }

        // 内容哈希去重：防止同一画面被重复触发（比如轮询和 Observer 同时命中）
        string hash;
        try
        {
            hash = await HashFileAsync(localPath).ConfigureAwait(false);
        }
        catch
        {
            hash = Guid.NewGuid().ToString("N");
        }

        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (hash == _lastHash && (now - _lastEmit).TotalMilliseconds < _cfg.DedupWindowMs)
            {
                Log.Debug($"截图内容重复，忽略（{source}）");
                TryDelete(localPath);
                return;
            }
            _lastHash = hash;
            _lastEmit = now;
        }

        Log.Info($"捕获到新截图（{source}）: {fileName}");
        OnStatus?.Invoke($"捕获新截图（{source}）");

        // 清理远端临时文件
        _ = Task.Run(async () => await _adb.CleanupRemoteAsync(_serial, CancellationToken.None)
            .ConfigureAwait(false));

        try
        {
            OnScreenshot?.Invoke(localPath);
        }
        catch (Exception ex)
        {
            Log.Error("截图回调处理异常", ex);
        }

        if (!_cfg.KeepImages)
            TryDelete(localPath);
        else
            TrimOldImages();
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            64 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(fs).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private void TrimOldImages()
    {
        try
        {
            var max = Math.Max(10, _cfg.MaxKeptImages);
            var files = new DirectoryInfo(CaptureDir).GetFiles("cap_*.png");
            if (files.Length <= max) return;

            foreach (var f in files.OrderByDescending(f => f.CreationTimeUtc).Skip(max))
                TryDelete(f.FullName);
        }
        catch (Exception ex)
        {
            Log.Debug($"清理旧截图失败: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose() => Stop();
}
