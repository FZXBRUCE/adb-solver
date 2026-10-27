using System.Security.Cryptography;
using AdbSolver.Core;

namespace AdbSolver.Adb;

/// <summary>
/// 截图监听器。
///
/// 三条独立的检测通道，任意一条命中就抓图，互相兜底：
///   1. MediaStore 轮询 —— content query 查最新图片记录（_id + date_added + _data）
///   2. 目录轮询     —— ls -t 直接扫截图目录，取最新文件
///   3. 屏幕差异     —— screencap 采样对比，作为最后一道保险（可选，默认关）
///
/// 抓到"新截图"之后，优先把设备上那个原始文件 pull 回来（快、且是用户截图那一刻的画面），
/// 拿不到路径才退回 screencap 现场截屏。
///
/// 全程只在 PC 侧通过 adb 操作，手机上不装任何东西。
/// </summary>
public sealed class ScreenshotWatcher : IDisposable
{
    private readonly AdbClient _adb;
    private readonly CaptureConfig _cfg;
    private readonly string _serial;

    private CancellationTokenSource? _cts;
    private Task? _mediaTask;
    private Task? _dirTask;
    private readonly object _lock = new();

    // 去重状态
    private long _lastMediaId = -1;
    private long _lastDateAdded = -1;
    private string _lastDirFile = "";
    private long _lastDirMtime = -1;
    private string _lastHash = "";
    private DateTime _lastEmit = DateTime.MinValue;

    private readonly TaskCompletionSource _primed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<string>? OnScreenshot;
    public event Action<string>? OnStatus;

    public bool Running { get; private set; }

    /// <summary>当前监听的设备序列号。</summary>
    public string Serial => _serial;

    /// <summary>诊断信息：最近一次各通道的原始返回，供 UI 排查。</summary>
    public string LastDiagnostics { get; private set; } = "";

    public ScreenshotWatcher(AdbClient adb, CaptureConfig cfg, string serial)
    {
        _adb = adb;
        _cfg = cfg;
        _serial = serial;
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

    public void Start()
    {
        lock (_lock)
        {
            if (Running) return;
            _cts = new CancellationTokenSource();
            Running = true;
        }

        var ct = _cts!.Token;

        _ = Task.Run(async () =>
        {
            try { await PrimeBaselineAsync(ct).ConfigureAwait(false); }
            finally { _primed.TrySetResult(); }
        }, ct);

        _mediaTask = Task.Run(() => MediaPollLoopAsync(ct), ct);
        _dirTask = Task.Run(() => DirPollLoopAsync(ct), ct);

        OnStatus?.Invoke("截图监听已启动");
        Log.Info($"ScreenshotWatcher 启动 serial={_serial}");
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

    // ================= 基线 =================

    private async Task PrimeBaselineAsync(CancellationToken ct)
    {
        try
        {
            var img = await _adb.QueryLatestImageAsync(_serial, ct).ConfigureAwait(false);
            if (img is not null)
            {
                _lastMediaId = img.Id;
                _lastDateAdded = img.DateAdded;
                Log.Info($"媒体库基线: _id={img.Id} date_added={img.DateAdded} path={img.DataPath}");
            }
            else
            {
                Log.Warn("媒体库基线为空，可能没有图片或 provider 不可访问");
            }

            var file = await _adb.FindNewestScreenshotFileAsync(_serial, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(file))
            {
                _lastDirFile = file;
                var st = await _adb.StatRemoteAsync(_serial, file, ct).ConfigureAwait(false);
                _lastDirMtime = st?.Mtime ?? -1;
                Log.Info($"目录基线: {file} mtime={_lastDirMtime}");
            }

            LastDiagnostics = $"基线 媒体库_id={_lastMediaId} 目录={_lastDirFile}";
            OnStatus?.Invoke($"监听就绪（当前最新截图: {Path.GetFileName(_lastDirFile)}）");
        }
        catch (Exception ex)
        {
            Log.Warn($"建立基线失败: {ex.Message}");
            LastDiagnostics = "基线失败: " + ex.Message;
        }
    }

    // ================= 通道一：MediaStore =================

    private async Task MediaPollLoopAsync(CancellationToken ct)
    {
        try { await _primed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (TimeoutException) { }

        var interval = Math.Max(200, _cfg.PollIntervalMs);
        var errCount = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var img = await _adb.QueryLatestImageAsync(_serial, ct).ConfigureAwait(false);
                if (img is not null && IsNewMedia(img))
                {
                    _lastMediaId = img.Id;
                    _lastDateAdded = img.DateAdded;
                    LastDiagnostics = $"媒体库命中 _id={img.Id} path={img.DataPath}";
                    await EmitAsync("MediaStore", img.DataPath, ct).ConfigureAwait(false);
                }
                errCount = 0;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                errCount++;
                if (errCount <= 3 || errCount % 20 == 0)
                    Log.Debug($"媒体库轮询异常(#{errCount}): {ex.Message}");
            }

            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private bool IsNewMedia(AdbClient.MediaImage img)
    {
        if (img.Id > _lastMediaId) return true;
        if (img.Id == _lastMediaId) return false;
        // _id 变小说明库被重建/清理过，用时间兜底
        return img.DateAdded > _lastDateAdded;
    }

    // ================= 通道二：目录扫描 =================

    private async Task DirPollLoopAsync(CancellationToken ct)
    {
        try { await _primed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (TimeoutException) { }

        var interval = Math.Max(300, _cfg.PollIntervalMs);
        var errCount = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var file = await _adb.FindNewestScreenshotFileAsync(_serial, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(file))
                {
                    var st = await _adb.StatRemoteAsync(_serial, file, ct).ConfigureAwait(false);
                    var mtime = st?.Mtime ?? -1;

                    var isNew = file != _lastDirFile ||
                                (mtime > 0 && _lastDirMtime > 0 && mtime > _lastDirMtime);

                    if (isNew)
                    {
                        _lastDirFile = file;
                        if (mtime > 0) _lastDirMtime = mtime;
                        LastDiagnostics = $"目录命中 {file} mtime={mtime}";
                        await EmitAsync("DirScan", file, ct).ConfigureAwait(false);
                    }
                }
                errCount = 0;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                errCount++;
                if (errCount <= 3 || errCount % 20 == 0)
                    Log.Debug($"目录轮询异常(#{errCount}): {ex.Message}");
            }

            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ================= 抓图 =================

    /// <summary>
    /// 抓到一张新截图。
    /// remotePath 有值且能 pull 就用原始文件（用户截图那一刻的画面），
    /// 否则退回 screencap 现场截屏。
    /// </summary>
    private async Task EmitAsync(string source, string remotePath, CancellationToken ct)
    {
        if (_cfg.SettleDelayMs > 0)
        {
            try { await Task.Delay(_cfg.SettleDelayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        var fileName = $"cap_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
        var localPath = Path.Combine(CaptureDir, fileName);

        var ok = false;
        if (!string.IsNullOrWhiteSpace(remotePath))
        {
            ok = await _adb.PullRemoteFileAsync(_serial, remotePath, localPath, ct).ConfigureAwait(false);
            if (!ok)
                Log.Warn($"拉取原始截图失败，回退到 screencap: {remotePath}");
        }

        if (!ok)
            ok = await _adb.PullScreenshotAsync(_serial, localPath, ct).ConfigureAwait(false);

        if (!ok)
        {
            Log.Warn($"截图获取失败（来源 {source}）");
            OnStatus?.Invoke("截图获取失败，检查设备连接");
            return;
        }

        // 内容哈希去重：两条通道同时命中同一张图时只处理一次
        string hash;
        try { hash = await HashFileAsync(localPath).ConfigureAwait(false); }
        catch { hash = Guid.NewGuid().ToString("N"); }

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

        Log.Info($"捕获新截图（{source}）: {fileName}");
        OnStatus?.Invoke($"捕获新截图（{source}）");

        try { OnScreenshot?.Invoke(localPath); }
        catch (Exception ex) { Log.Error("截图回调异常", ex); }

        if (!_cfg.KeepImages) TryDelete(localPath);
        else TrimOldImages();
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
            var files = new DirectoryInfo(CaptureDir).GetFiles("cap_*.*");
            if (files.Length <= max) return;
            foreach (var f in files.OrderByDescending(f => f.CreationTimeUtc).Skip(max))
                TryDelete(f.FullName);
        }
        catch (Exception ex) { Log.Debug($"清理旧截图失败: {ex.Message}"); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose() => Stop();
}
