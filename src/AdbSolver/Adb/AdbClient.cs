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
        var r = await ProcRunner.RunAsync(AdbPath, full, to, ct).ConfigureAwait(false);

        if (r.TimedOut)
            Log.Warn($"adb 超时: adb {string.Join(' ', full)}");
        else if (!r.Ok)
            Log.Debug($"adb 返回 {r.ExitCode}: {r.Combined}");

        return r;
    }

    /// <summary>adb devices -l 解析出设备列表。</summary>
    public async Task<List<DeviceInfo>> ListDevicesAsync(CancellationToken ct = default)
    {
        var r = await ProcRunner.RunAsync(AdbPath, new[] { "devices", "-l" }, Timeout, ct).ConfigureAwait(false);
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
        var r = await ProcRunner.RunAsync(AdbPath, new[] { "connect", hostPort }, TimeSpan.FromSeconds(12), ct)
            .ConfigureAwait(false);
        var msg = r.Combined;
        var ok = r.Ok && !msg.Contains("failed", StringComparison.OrdinalIgnoreCase)
                       && !msg.Contains("cannot", StringComparison.OrdinalIgnoreCase);
        return (ok, msg.Length == 0 ? "(无输出)" : msg);
    }

    public async Task DisconnectAsync(string hostPort, CancellationToken ct = default)
    {
        await ProcRunner.RunAsync(AdbPath, new[] { "disconnect", hostPort }, TimeSpan.FromSeconds(8), ct)
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
        foreach (var line in r.StdOut.Split('\n'))
        {
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

        var pull = await RunAsync(new[] { "pull", remote, localPath }, serial, ct, 25000)
            .ConfigureAwait(false);
        if (!pull.Ok)
        {
            Log.Warn($"adb pull 失败: {pull.Combined}");
            return false;
        }

        try
        {
            if (!File.Exists(localPath) || new FileInfo(localPath).Length < 1024)
            {
                Log.Warn("拉取到的截图文件异常（不存在或过小）");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("检查截图文件失败", ex);
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
        var r = await ProcRunner.RunAsync(AdbPath, new[] { "start-server" }, TimeSpan.FromSeconds(20), ct)
            .ConfigureAwait(false);
        if (!r.Ok)
            Log.Warn($"adb start-server 失败: {r.Combined}");
        return r.Ok;
    }

    public async Task KillServerAsync(CancellationToken ct = default)
    {
        try
        {
            await ProcRunner.RunAsync(AdbPath, new[] { "kill-server" }, TimeSpan.FromSeconds(8), ct)
                .ConfigureAwait(false);
        }
        catch { }
    }
}
