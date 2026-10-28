using System.Text;
using AdbSolver.Adb;
using AdbSolver.Core;
using AdbSolver.UI;

namespace AdbSolver;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 全局兜底：任何未捕获异常都写日志并提示，而不是直接闪退
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log.Error("未捕获异常（AppDomain）", ex ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
            ShowCrash(ex);
        };

        Application.ThreadException += (_, e) =>
        {
            Log.Error("未捕获异常（UI 线程）", e.Exception);
            ShowCrash(e.Exception);
        };

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        try
        {
            ApplicationConfiguration.Initialize();
        }
        catch
        {
            // EnableVisualStyles() 内部会往 %TEMP% 写一个 activation context 清单文件。
            // 目标机器 TEMP 被锁（AppLocker / ASR / 组策略 / 安全软件）时它会抛
            //   UnauthorizedAccessException: Access to the path 'C:\...\Temp\xxxx.tmp' is denied
            // 以前的兜底又调了一遍 EnableVisualStyles()，等于在同一条死路上再撞一次，
            // 结果就是"换台电脑一启动就闪退"。
            // 这里改成：视觉样式要不到就算了，绝不能让它挡住程序启动。
            try { Application.EnableVisualStyles(); } catch { /* 没有视觉样式也能跑 */ }
            try { Application.SetCompatibleTextRenderingDefault(false); } catch { }
        }

        // 控制台模式下用于自检
        if (args.Length > 0 && args[0] is "--selftest" or "-t")
        {
            RunSelfTest();
            return;
        }

        // 诊断模式：不开窗口，直接验证截图监听能不能拿到图
        if (args.Length > 0 && args[0] is "--probe" or "-p")
        {
            RunProbe().GetAwaiter().GetResult();
            return;
        }

        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            Log.Error("主窗口异常退出", ex);
            ShowCrash(ex);
        }
    }

    private static void ShowCrash(Exception? ex)
    {
        try
        {
            var msg = new StringBuilder();
            msg.AppendLine("程序出错了。详细信息已写入日志：");
            msg.AppendLine(Path.Combine(AppPaths.LogDir, "adbsolver-*.log"));
            msg.AppendLine();
            if (ex is not null)
            {
                msg.AppendLine(ex.GetType().Name + ": " + ex.Message);
                if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                    msg.AppendLine(ex.StackTrace.Length > 1500 ? ex.StackTrace[..1500] : ex.StackTrace);
            }
            MessageBox.Show(msg.ToString(), "AdbSolver 错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }

    /// <summary>--selftest：不开窗口，快速验证配置/ADB/端点是否可用。</summary>
    private static void RunSelfTest()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== AdbSolver 自检 ===");
        sb.AppendLine($"数据目录 : {AppPaths.DataDir}");
        sb.AppendLine($"配置路径 : {AppConfig.DefaultPath}");

        var cfg = AppConfig.Load();
        sb.AppendLine($"端点数量 : {cfg.Endpoints.Count}（启用 {cfg.Endpoints.Count(e => e.Enabled)}）");

        var adb = new AdbClient(cfg.Adb, cfg.Stealth);
        sb.AppendLine($"adb 路径 : {adb.AdbPath}");
        sb.AppendLine($"adb 存在 : {adb.AdbExists}");

        if (adb.AdbExists)
        {
            var devs = adb.ListDevicesAsync().GetAwaiter().GetResult();
            sb.AppendLine($"设备数量 : {devs.Count}");
            foreach (var d in devs)
            {
                sb.AppendLine($"  - {d.Summary}");
                if (!d.IsReady) continue;
                try
                {
                    var full = adb.GetDeviceInfoAsync(d).GetAwaiter().GetResult();
                    sb.AppendLine($"    详细 : {full.Summary}");
                }
                catch (Exception ex) { sb.AppendLine($"    详细 : 失败 {ex.Message}"); }
            }
        }

        sb.AppendLine("=== 自检结束 ===");
        var text = sb.ToString();
        Console.WriteLine(text);

        try { File.WriteAllText(Path.Combine(AppPaths.DataDir, "selftest.txt"), text, Encoding.UTF8); } catch { }
        try { File.WriteAllText(Path.Combine(AppPaths.AppDir, "selftest.txt"), text, Encoding.UTF8); } catch { }
        try { Console.Out.Flush(); } catch { }
    }

    /// <summary>
    /// --probe：不启动界面，直接跑一遍截图检测链路，把每一步的原始返回打印出来。
    /// 用来快速定位"监听不到截图"到底是哪一环坏了。
    /// </summary>
    private static async Task RunProbe()
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }

        W("=== 截图链路诊断 ===");

        var cfg = AppConfig.Load();
        var adb = new AdbClient(cfg.Adb, cfg.Stealth);
        W($"adb: {adb.AdbPath} (存在={adb.AdbExists})");
        if (!adb.AdbExists) { Flush(sb); return; }

        var devices = await adb.ListDevicesAsync();
        W($"设备数: {devices.Count}");
        foreach (var d in devices) W($"  - {d.Serial} [{d.State}] {d.Model}");

        var serial = devices.FirstOrDefault(d => d.IsReady)?.Serial;
        if (serial is null) { W("没有就绪设备，退出"); Flush(sb); return; }
        W($"使用设备: {serial}");

        // 1. 媒体库
        W("\n[1] 媒体库最新图片");
        var img = await adb.QueryLatestImageAsync(serial);
        if (img is null) W("  → 查不到（provider 不可用 / 库里没图）");
        else W($"  → _id={img.Id} date_added={img.DateAdded}\n     path={img.DataPath}");

        // 2. 截图目录
        W("\n[2] 目录扫描最新截图");
        var file = await adb.FindNewestScreenshotFileAsync(serial);
        if (file is null) W("  → 一个截图目录都没找到文件");
        else
        {
            W($"  → {file}");
            var st = await adb.StatRemoteAsync(serial, file);
            W($"     mtime={st?.Mtime} size={st?.Size}");
        }

        // 3. 拉取
        W("\n[3] 拉取测试");
        var local = Path.Combine(AppPaths.DataDir, "probe_pull.png");
        if (file is not null)
        {
            var ok = await adb.PullRemoteFileAsync(serial, file, local);
            W($"  pull 原始截图: {(ok ? "成功 " + new FileInfo(local).Length + " 字节" : "失败")}");
        }
        var local2 = Path.Combine(AppPaths.DataDir, "probe_cap.png");
        var ok2 = await adb.PullScreenshotAsync(serial, local2);
        W($"  screencap 现场截屏: {(ok2 ? "成功 " + new FileInfo(local2).Length + " 字节" : "失败")}");

        // 4. 实时监听
        W("\n[4] 实时监听 25 秒 —— 现在去手机上截个图");
        var watcher = new ScreenshotWatcher(adb, cfg.Capture, serial, cfg.Stealth);
        var hits = new List<string>();
        watcher.OnScreenshot += p => { lock (hits) hits.Add(p); W($"  ★ 命中: {p}"); };
        watcher.OnStatus += s => W($"  · {s}");
        watcher.Start();

        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            if (hits.Count > 0) break;
        }
        watcher.Stop();
        watcher.Dispose();

        W($"\n结果: 命中 {hits.Count} 次");
        W($"诊断: {watcher.LastDiagnostics}");
        Flush(sb);
    }

    private static void Flush(StringBuilder sb)
    {
        try { File.WriteAllText(Path.Combine(AppPaths.DataDir, "probe.txt"), sb.ToString(), Encoding.UTF8); } catch { }
        try { Console.Out.Flush(); } catch { }
    }
}
