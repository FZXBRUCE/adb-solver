using System.Text.RegularExpressions;
using AdbSolver.Core;

namespace AdbSolver.Adb;

/// <summary>
/// ADB 封装层。负责：定位 adb、枚举设备、WiFi 连接、取设备属性、截图拉取。
/// 全部通过命令行完成，不依赖任何第三方库。
/// </summary>
public sealed class AdbClient
{
    private readonly AdbConfig _cfg;
    private string? _adbPath;

    public AdbClient(AdbConfig cfg) => _cfg = cfg;

    public string AdbPath => _adbPath ??= ResolveAdbPath();

    /// <summary>自动探测 adb.exe：配置 > 程序目录 > PATH > 常见安装位置。</summary>
    private string ResolveAdbPath()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.AdbPath) && File.Exists(_cfg.AdbPath))
            return _cfg.AdbPath;

        // 1) 程序目录及子目录
        foreach (var name in new[] { "adb.exe", Path.Combine("platform-tools", "adb.exe") })
        {
            var p = Path.Combine(AppPaths.AppDir, name);
            if (File.Exists(p)) return p;
        }

        // 2) PATH
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "adb.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }

        // 3) 常见安装位置
        var candidates = new List<string>();
        foreach (var drive in new[] { "C:", "D:", "E:" })
        {
            candidates.Add($@"{drive}\adb\adb.exe");
            candidates.Add($@"{drive}\platform-tools\adb.exe");
            candidates.Add($@"{drive}\Android\platform-tools\adb.exe");
        }
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe"));
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.Add(Path.Combine(programFiles, "Android", "platform-tools", "adb.exe"));

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        return "adb"; // 兜底，交给 PATH 解析（找不到时会报错，由上层提示）
    }

    public bool AdbExists
    {
        get
        {
            var p = AdbPath;
            return p == "adb" || File.Exists(p);
        }
    }

    private TimeSpan Timeout => TimeSpan.FromMilliseconds(Math.Max(3000, _cfg.CommandTimeoutMs));

    /// <summary>执行一条 adb 命令（自动拼 -s serial）。</summary>
    public async Task<ProcResult> RunAsync(
        IEnumerable<string> args,
        string? serial = null,
        CancellationToken ct = default,
        int? timeoutMs = null)
    {
        var full = new List<string>();
        var target = serial ?? _cfg.DeviceSerial;
        if (!string.IsNullOrWhiteSpace(target))
        {
            full.Add("-s");
            full.Add(target.Trim());
        }
        full.AddRange(args);

        var to = timeoutMs.HasValue ? TimeSpan.FromMilliseconds(timeoutMs.Value) : Timeout;
        var r = await ProcRunner.RunAsync(AdbPath, full, to, ct, null, ChildEnv).ConfigureAwait(false);

        if (r.TimedOut)
            Log.Warn($"adb 超时: adb {string.Join(' ', full)}");
        else if (!r.Ok)
            Log.Debug($"adb 返回 {r.ExitCode}: {r.Combined}");

        return r;
    }

    /// <summary>
    /// 给 adb 子进程的环境变量。
    /// 系统 %TEMP% 被 AppLocker / 组策略 / 安全软件锁掉时，adb server 启动会报
    ///   error: cannot open 'C:\...\Temp\adb.log': Permission denied
    /// 然后 daemon 起不来，表现就是"设备列表永远是空的"。
    /// 把 TEMP/TMP 指到我们自己的可写目录，adb 就能正常拉起来。
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ChildEnv
    {
        get
        {
            try
            {
                var tmp = AppPaths.TempDir;
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TEMP"] = tmp,
                    ["TMP"] = tmp,
                };
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>adb devices -l 解析出设备列表。</summary>
    public async Task<List<DeviceInfo>> ListDevicesAsync(CancellationToken ct = default)
    {
        var r = await ProcRunner.RunAsync(AdbPath, new[] { "devices", "-l" }, Timeout, ct, null, ChildEnv).ConfigureAwait(false);
        var list = new List<DeviceInfo>();
        if (!r.Ok && string.IsNullOrWhiteSpace(r.StdOut))
            return list;

        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("*", StringComparison.Ordinal)) continue; // daemon 日志

            // 格式: <serial>\t<state> [key:value ...]
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var serial = parts[0];
            var state = parts[1];
            var model = "";
            var conn = serial.Contains(':') ? "WiFi" : "USB";

            for (var i = 2; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("model:", StringComparison.Ordinal))
                    model = parts[i]["model:".Length..].Replace('_', ' ');
            }

            list.Add(new DeviceInfo
            {
                Serial = serial,
                State = state,
                Model = model,
                Connection = conn,
            });
        }

        return list;
    }

    /// <summary>adbf connect host:port（WiFi / 无线调试）。</summary>
    public async Task<(bool Ok, string Message)> ConnectAsync(string hostPort, CancellationToken ct = default)
    {
        var r = await ProcRunner.RunAsync(AdbPath, new[] { "connect", hostPort }, TimeSpan.FromSeconds(12), ct, null, ChildEnv)
            .ConfigureAwait(false);
        var msg = r.Combined;
        var ok = r.Ok && !msg.Contains("failed", StringComparison.OrdinalIgnoreCase)
                       && !msg.Contains("cannot", StringComparison.OrdinalIgnoreCase);
        return (ok, msg.Length == 0 ? "(无输出)" : msg);
    }

    public async Task DisconnectAsync(string hostPort, CancellationToken ct = default)
    {
        await ProcRunner.RunAsync(AdbPath, new[] { "disconnect", hostPort }, TimeSpan.FromSeconds(8), ct, null, ChildEnv)
            .ConfigureAwait(false);
    }

    /// <summary>取一台设备的完整信息（型号/系统/分辨率/电量/连接方式）。</summary>
    public async Task<DeviceInfo> GetDeviceInfoAsync(DeviceInfo baseInfo, CancellationToken ct = default)
    {
        var serial = baseInfo.Serial;
        if (baseInfo.State != "device")
            return baseInfo;

        var props = await GetPropsAsync(serial, ct).ConfigureAwait(false);

        var brand = Prop(props, "ro.product.brand", "ro.product.manufacturer");
        var model = Prop(props, "ro.product.model", "ro.product.marketname");
        if (string.IsNullOrWhiteSpace(model)) model = baseInfo.Model;
        var android = Prop(props, "ro.build.version.release");
        var sdk = Prop(props, "ro.build.version.sdk");

        // 分辨率：优先 wm size 覆盖值
        var res = "";
        var wm = await RunAsync(new[] { "shell", "wm", "size" }, serial, ct, 8000).ConfigureAwait(false);
        var m = Regex.Match(wm.StdOut, @"(\d{3,5})\s*[xX]\s*(\d{3,5})");
        if (m.Success) res = $"{m.Groups[1].Value}x{m.Groups[2].Value}";

        // 电量
        var battery = -1;
        var dumpsys = await RunAsync(new[] { "shell", "dumpsys", "battery" }, serial, ct, 8000)
            .ConfigureAwait(false);
        var bm = Regex.Match(dumpsys.StdOut, @"level:\s*(\d+)");
        if (bm.Success && int.TryParse(bm.Groups[1].Value, out var lv)) battery = lv;

        return new DeviceInfo
        {
            Serial = serial,
            State = baseInfo.State,
            Model = model.Trim(),
            Brand = Capitalize(brand.Trim()),
            AndroidVersion = android.Trim(),
            Sdk = sdk.Trim(),
            Resolution = res,
            BatteryLevel = battery,
            Connection = baseInfo.Connection,
        };
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ================= MediaStore 查询 =================

    /// <summary>MediaStore 里的一条图片记录。</summary>
    public sealed record MediaImage(long Id, long DateAdded, string DataPath);

    /// <summary>
    /// 查询媒体库里最新的一张图片。
    ///
    /// 两个坑都踩过，记在这里免得再犯：
    ///   1. `content query` 的 --sort 只接受 SQL 排序表达式，**不接受 LIMIT**。
    ///      塞 "date_added DESC LIMIT 1" 会抛 java.lang.IllegalArgumentException: Invalid token LIMIT
    ///   2. 整个 shell 命令必须**拼成一个字符串**传给 adb。
    ///      如果拆成 "--sort" 和 "date_added DESC" 两个参数，adb 会把它们当独立参数转发，
    ///      设备端 content 只收到 --sort 没有值，直接打 usage 退出。
    /// </summary>
    public async Task<MediaImage?> QueryLatestImageAsync(string serial, CancellationToken ct = default)
    {
        var cmd = "content query --uri content://media/external/images/media " +
                  "--projection _id:date_added:_data --sort 'date_added DESC'";

        var r = await RunAsync(new[] { "shell", cmd }, serial, ct, 10000).ConfigureAwait(false);
        return ParseFirstMediaRow(r.StdOut);
    }

    /// <summary>查媒体库里最新的一张"截图"（按路径过滤 Screenshot 关键字）。</summary>
    public async Task<MediaImage?> QueryLatestScreenshotAsync(string serial, CancellationToken ct = default)
    {
        var cmd = "content query --uri content://media/external/images/media " +
                  "--projection _id:date_added:_data --where \"_data LIKE '%creenshot%'\" " +
                  "--sort 'date_added DESC'";

        var r = await RunAsync(new[] { "shell", cmd }, serial, ct, 10000).ConfigureAwait(false);
        return ParseFirstMediaRow(r.StdOut);
    }

    private static MediaImage? ParseFirstMediaRow(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;

        foreach (var line in stdout.Split('\n'))
        {
            var s = line.Trim();
            if (!s.StartsWith("Row:", StringComparison.Ordinal)) continue;

            var id = MatchLong(s, @"_id=(\d+)");
            if (id < 0) continue;
            var dateAdded = MatchLong(s, @"date_added=(\d+)");
            var data = MatchStr(s, @"_data=([^,\r\n]*)");
            return new MediaImage(id, dateAdded < 0 ? 0 : dateAdded, data ?? "");
        }

        return null;
    }

    /// <summary>
    /// 用 ls -t 直接扫截图目录，拿最新文件。
    /// MediaStore 不可用（被 ROM 限制 / 没索引）时的兜底方案。
    /// </summary>
    public async Task<string?> FindNewestScreenshotFileAsync(string serial, CancellationToken ct = default)
    {
        var dirs = new[]
        {
            "/sdcard/Pictures/Screenshots",
            "/sdcard/DCIM/Screenshots",
            "/sdcard/Screenshots",
            "/sdcard/Pictures",
        };

        foreach (var dir in dirs)
        {
            var r = await RunAsync(
                new[] { "shell", $"ls -t '{dir}' 2>/dev/null | head -20" },
                serial, ct, 8000).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(r.StdOut)) continue;

            foreach (var line in r.StdOut.Split('\n'))
            {
                var name = line.Trim();
                if (name.Length == 0) continue;
                if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                    continue;

                return $"{dir}/{name}";
            }
        }

        return null;
    }

    /// <summary>取远端文件的 mtime 和大小，用于判断是不是新文件。</summary>
    public async Task<(long Mtime, long Size)?> StatRemoteAsync(string serial, string remotePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(remotePath)) return null;

        var r = await RunAsync(
            new[] { "shell", $"stat -c '%Y %s' '{remotePath}' 2>/dev/null" },
            serial, ct, 8000).ConfigureAwait(false);

        var parts = r.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        if (!long.TryParse(parts[0], out var mtime)) return null;
        if (!long.TryParse(parts[1], out var size)) return null;
        return (mtime, size);
    }

    private static long MatchLong(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? v : -1;
    }

    private static string? MatchStr(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string Prop(Dictionary<string, string> props, params string[] keys)
    {
        foreach (var k in keys)
            if (props.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return "";
    }

    /// <summary>一次性 getprop 全量读取（比逐条快几十倍）。</summary>
    private async Task<Dictionary<string, string>> GetPropsAsync(string serial, CancellationToken ct)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var r = await RunAsync(new[] { "shell", "getprop" }, serial, ct, 10000).ConfigureAwait(false);

        // 坑：设备返回的行尾是 \r\n，按 \n 切完每行尾巴上还挂着一个 \r。
        // 正则里的 \]$ 会因为那个 \r 匹配失败，结果整个字典是空的，
        // 界面就显示成 "Android  (API )" —— 型号靠兜底、版本全空。
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            if (line.Length == 0) continue;

            // [key]: [value]
            var m = Regex.Match(line, @"^\[(?<k>[^\]]+)\]:\s*\[(?<v>.*)\]$");
            if (m.Success) dict[m.Groups["k"].Value] = m.Groups["v"].Value;
        }
        return dict;
    }

    /// <summary>把设备截图拉到本地文件。成功返回 true。</summary>
    public async Task<bool> PullScreenshotAsync(string serial, string localPath, CancellationToken ct = default)
    {
        var remote = "/sdcard/adbsolver_cap.png";

        // screencap -p 直接写远端文件
        var cap = await RunAsync(new[] { "shell", "screencap", "-p", remote }, serial, ct, 15000)
            .ConfigureAwait(false);
        if (!cap.Ok)
        {
            Log.Warn($"screencap 失败: {cap.Combined}");
            return false;
        }

        var ok = await PullRemoteFileAsync(serial, remote, localPath, ct).ConfigureAwait(false);
        _ = CleanupRemoteAsync(serial, CancellationToken.None);
        return ok;
    }

    /// <summary>
    /// 直接把设备上已存在的文件（比如系统刚存的截图）拉到本地。
    /// 比 screencap 快得多，而且拿到的就是"用户截图那一刻"的画面，不会因为屏幕变化而错位。
    /// </summary>
    public async Task<bool> PullRemoteFileAsync(string serial, string remotePath, string localPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(remotePath)) return false;

        try
        {
            var dir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch { }

        var pull = await RunAsync(new[] { "pull", remotePath, localPath }, serial, ct, 25000)
            .ConfigureAwait(false);
        if (!pull.Ok)
        {
            Log.Warn($"adb pull 失败 [{remotePath}]: {pull.Combined}");
            return false;
        }

        try
        {
            if (!File.Exists(localPath) || new FileInfo(localPath).Length < 1024)
            {
                Log.Warn("拉取到的文件异常（不存在或过小）");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("检查拉取文件失败", ex);
            return false;
        }
    }

    /// <summary>删除设备上的临时截图。</summary>
    public async Task CleanupRemoteAsync(string serial, CancellationToken ct = default)
    {
        try
        {
            await RunAsync(new[] { "shell", "rm", "-f", "/sdcard/adbsolver_cap.png" }, serial, ct, 6000)
                .ConfigureAwait(false);
        }
        catch { }
    }

    /// <summary>拉起 adb server（有些环境需要显式 start-server）。</summary>
    public async Task<bool> StartServerAsync(CancellationToken ct = default)
    {
        var r = await ProcRunner.RunAsync(AdbPath, new[] { "start-server" }, TimeSpan.FromSeconds(20), ct, null, ChildEnv)
            .ConfigureAwait(false);
        if (!r.Ok)
            Log.Warn($"adb start-server 失败: {r.Combined}");
        return r.Ok;
    }

    public async Task KillServerAsync(CancellationToken ct = default)
    {
        try
        {
            await ProcRunner.RunAsync(AdbPath, new[] { "kill-server" }, TimeSpan.FromSeconds(8), ct, null, ChildEnv)
                .ConfigureAwait(false);
        }
        catch { }
    }
}
