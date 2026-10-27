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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        }

        // 控制台模式下用于自检
        if (args.Length > 0 && args[0] is "--selftest" or "-t")
        {
            RunSelfTest();
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

        var adb = new AdbClient(cfg.Adb);
        sb.AppendLine($"adb 路径 : {adb.AdbPath}");
        sb.AppendLine($"adb 存在 : {adb.AdbExists}");

        if (adb.AdbExists)
        {
            var devs = adb.ListDevicesAsync().GetAwaiter().GetResult();
            sb.AppendLine($"设备数量 : {devs.Count}");
            foreach (var d in devs)
                sb.AppendLine($"  - {d.Summary}");
        }

        sb.AppendLine("=== 自检结束 ===");
        var text = sb.ToString();
        Console.WriteLine(text);

        try { File.WriteAllText(Path.Combine(AppPaths.DataDir, "selftest.txt"), text, Encoding.UTF8); } catch { }
        try { File.WriteAllText(Path.Combine(AppPaths.AppDir, "selftest.txt"), text, Encoding.UTF8); } catch { }
        try { Console.Out.Flush(); } catch { }
    }
}
