using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdbSolver.Core;

/// <summary>AI 供应商类型。OpenAI 兼容协议覆盖了绝大多数中转站。</summary>
public enum AiProviderKind
{
    /// <summary>OpenAI /v1/chat/completions 兼容协议（DeepSeek、Kimi、通义、OneAPI、NewAPI、各类中转站）。</summary>
    OpenAiCompatible = 0,
    /// <summary>Anthropic Claude /v1/messages 原生协议。</summary>
    Anthropic = 1,
    /// <summary>Google Gemini generateContent 原生协议。</summary>
    Gemini = 2,
}

public sealed class AiEndpointConfig
{
    public string Name { get; set; } = "默认";
    public AiProviderKind Kind { get; set; } = AiProviderKind.OpenAiCompatible;
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "deepseek-chat";

    /// <summary>是否启用。禁用的端点不参与故障转移。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 该端点承担哪些角色：Ocr=识图+切题, Solve=作答。列表为空表示全都干。
    /// </summary>
    public List<string> Roles { get; set; } = new();

    public bool Supports(string role) =>
        Roles.Count == 0 || Roles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));

    public AiEndpointConfig Clone() => new()
    {
        Name = Name,
        Kind = Kind,
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        Model = Model,
        Enabled = Enabled,
        Roles = new List<string>(Roles),
    };
}

public sealed class AdbConfig
{
    /// <summary>adb.exe 路径。留空则自动探测：程序目录 -> PATH -> 常见安装位置。</summary>
    public string AdbPath { get; set; } = "";

    /// <summary>要连接的设备序列号。留空 = 自动选第一台在线设备。</summary>
    public string DeviceSerial { get; set; } = "";

    /// <summary>WiFi 连接的 host:port 列表，启动时自动 adb connect。</summary>
    public List<string> WifiTargets { get; set; } = new();

    /// <summary>启动时自动执行 adb connect。</summary>
    public bool AutoConnectWifi { get; set; } = true;

    /// <summary>截图拉取的本地缓存目录。</summary>
    public string CaptureDir { get; set; } = "";

    /// <summary>单条 adb 命令超时（毫秒）。</summary>
    public int CommandTimeoutMs { get; set; } = 20000;
}

public sealed class CaptureConfig
{
    /// <summary>
    /// 监听方式。现在两条通道（MediaStore 查询 / 目录扫描）始终并行跑，
    /// 这个字段只用于兼容旧配置和日志显示，不再决定行为。
    /// </summary>
    public CaptureMode Mode { get; set; } = CaptureMode.Hybrid;

    /// <summary>轮询间隔（毫秒）。越小越灵敏，越大越省电。</summary>
    public int PollIntervalMs { get; set; } = 250;

    /// <summary>去重时间窗（毫秒）。同一张图在这段时间内不重复处理。</summary>
    public int DedupWindowMs { get; set; } = 2500;

    /// <summary>
    /// 截图后等待文件写盘完成的额外延时（毫秒）。
    /// 默认 0：监听到的本来就是已落盘的文件，再等纯属白等；
    /// 个别机型拉到半张图时再加到 100~200。
    /// </summary>
    public int SettleDelayMs { get; set; } = 0;

    /// <summary>是否保留截图原图。</summary>
    public bool KeepImages { get; set; } = true;

    /// <summary>保留图片时最多保留多少张。</summary>
    public int MaxKeptImages { get; set; } = 200;
}

public enum CaptureMode
{
    /// <summary>只用 ContentObserver 常驻监听。</summary>
    Observer = 0,
    /// <summary>只用定时轮询。</summary>
    Poll = 1,
    /// <summary>两者并行，Observer 为主、轮询兜底。</summary>
    Hybrid = 2,
}

/// <summary>
/// 反检测 / 隐身配置。
///
/// 这里能做的和不能做的，先说清楚，免得用户以为开了就无敌：
///
/// 【改不动的】目标 App 如果查 Settings.Global.ADB_ENABLED、扫 /proc/net/tcp 看
/// 有没有连 5037、或者直接调 UsbManager 看调试状态，PC 端无论怎么改都绕不过去——
/// 那些检测跑在手机上，主动权在 App 手里。本模块不声称能解决这类。
///
/// 【能改的】工具在设备上留下的痕迹、在 PC 上留下的痕迹、以及操作节奏：
///   1. 临时文件不再落 /sdcard（那是人尽可读的共享存储），改走 exec-out 直出，
///      连文件都不产生；真需要落盘时也只用随机名并立刻删除。
///   2. PC 端进程名、窗口标题、数据目录名可改，避免肉眼和任务管理器一眼认出。
///   3. 操作节奏加抖动，别像机器一样固定毫秒节奏。
/// </summary>
public sealed class StealthConfig
{
    /// <summary>总开关。关掉时所有行为与老版本完全一致。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 截图走 exec-out 直出内存，不在设备上产生任何文件。
    /// 关掉会退回老的 /sdcard 落盘 + pull 方式（会在共享存储留痕）。
    /// </summary>
    public bool CaptureInMemory { get; set; } = true;

    /// <summary>
    /// 必须落盘时的目录。默认 /data/local/tmp —— 比 /sdcard 强得多：
    /// 普通 App 没有权限读这个目录（Android 11+ 的 scoped storage 更是直接隔死），
    /// 而 shell 用户往里写是完全合法的，看起来跟系统临时文件没区别。
    /// </summary>
    public string RemoteTempDir { get; set; } = "/data/local/tmp";

    /// <summary>临时文件名是否随机化（不用固定名，避免按名字扫）。</summary>
    public bool RandomizeTempNames { get; set; } = true;

    /// <summary>PC 端窗口标题。留空则用默认标题。</summary>
    public string WindowTitle { get; set; } = "";

    /// <summary>轮询间隔的随机抖动比例（0~0.5）。0=固定节奏，0.3=±30%。</summary>
    public double PollJitter { get; set; } = 0.3;

    /// <summary>隐藏窗口标题里的版本号 / 工具名之类可识别字样。</summary>
    public bool DiscreetTitle { get; set; } = true;
}

public sealed class PipelineConfig
{
    /// <summary>识别到的文本最少多少字符才当作有效题目。</summary>
    public int MinQuestionChars { get; set; } = 8;

    /// <summary>是否把最终答案自动写入剪贴板。</summary>
    public bool CopyAnswerToClipboard { get; set; } = true;

    /// <summary>连续失败多少次后暂停自动流水线。</summary>
    public int MaxConsecutiveFailures { get; set; } = 5;

    /// <summary>作答风格提示词（可自定义）。</summary>
    public string AnswerStyle { get; set; } =
        "直接给出最终答案。如果是选择题，输出选项字母和选项内容；如果是填空题，只输出要填的内容；不要解释、不要寒暄、不要 Markdown 标题。";
}

public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;
    public AdbConfig Adb { get; set; } = new();
    public CaptureConfig Capture { get; set; } = new();
    public PipelineConfig Pipeline { get; set; } = new();
    public StealthConfig Stealth { get; set; } = new();
    public List<AiEndpointConfig> Endpoints { get; set; } = new();

    // ---- 持久化 ----

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath =>
        Path.Combine(AppPaths.DataDir, "config.json");

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOpts);
                if (cfg is not null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取配置失败，使用默认配置: {ex.Message}");
        }

        var fresh = new AppConfig();
        fresh.Normalize();
        return fresh;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"保存配置失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 深拷贝。设置对话框拿副本去改，只有点「保存」才写回原对象——
    /// 这样点「取消」或直接按 X 关掉时，内存里的配置也不会被偷偷改脏。
    /// </summary>
    public AppConfig Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Adb = new AdbConfig
        {
            AdbPath = Adb.AdbPath,
            DeviceSerial = Adb.DeviceSerial,
            WifiTargets = new List<string>(Adb.WifiTargets),
            AutoConnectWifi = Adb.AutoConnectWifi,
            CaptureDir = Adb.CaptureDir,
            CommandTimeoutMs = Adb.CommandTimeoutMs,
        },
        Capture = new CaptureConfig
        {
            Mode = Capture.Mode,
            PollIntervalMs = Capture.PollIntervalMs,
            DedupWindowMs = Capture.DedupWindowMs,
            SettleDelayMs = Capture.SettleDelayMs,
            KeepImages = Capture.KeepImages,
            MaxKeptImages = Capture.MaxKeptImages,
        },
        Pipeline = new PipelineConfig
        {
            MinQuestionChars = Pipeline.MinQuestionChars,
            CopyAnswerToClipboard = Pipeline.CopyAnswerToClipboard,
            MaxConsecutiveFailures = Pipeline.MaxConsecutiveFailures,
            AnswerStyle = Pipeline.AnswerStyle,
        },
        Stealth = new StealthConfig
        {
            Enabled = Stealth.Enabled,
            CaptureInMemory = Stealth.CaptureInMemory,
            RemoteTempDir = Stealth.RemoteTempDir,
            RandomizeTempNames = Stealth.RandomizeTempNames,
            WindowTitle = Stealth.WindowTitle,
            PollJitter = Stealth.PollJitter,
            DiscreetTitle = Stealth.DiscreetTitle,
        },
        Endpoints = Endpoints.Select(e => e.Clone()).ToList(),
    };

    /// <summary>把另一份配置的内容拷回自己（引用保持不变，下游持有的是同一个对象）。</summary>
    public void CopyFrom(AppConfig other)
    {
        SchemaVersion = other.SchemaVersion;
        Adb = other.Adb;
        Capture = other.Capture;
        Pipeline = other.Pipeline;
        Stealth = other.Stealth;
        Endpoints = other.Endpoints;
    }

    /// <summary>补齐空目录、默认端点等，保证下游不为 null。</summary>
    public void Normalize()
    {
        Adb ??= new AdbConfig();
        Capture ??= new CaptureConfig();
        Pipeline ??= new PipelineConfig();
        // 老版本 config.json 里没有 Stealth 节点，反序列化出来就是 null。
        // 这里必须补一个默认对象，否则下游任何 stealth.X 都是 NullReferenceException，
        // 表现就是"升级后一启动就闪退"。
        Stealth ??= new StealthConfig();
        Endpoints ??= new List<AiEndpointConfig>();
        Adb.WifiTargets ??= new List<string>();

        if (string.IsNullOrWhiteSpace(Stealth.RemoteTempDir))
            Stealth.RemoteTempDir = "/data/local/tmp";
        Stealth.PollJitter = Math.Clamp(Stealth.PollJitter, 0, 0.5);

        if (string.IsNullOrWhiteSpace(Adb.CaptureDir))
            Adb.CaptureDir = Path.Combine(AppPaths.DataDir, "captures");

        if (Endpoints.Count == 0)
        {
            Endpoints.Add(new AiEndpointConfig
            {
                Name = "DeepSeek",
                Kind = AiProviderKind.OpenAiCompatible,
                BaseUrl = "https://api.deepseek.com/v1",
                Model = "deepseek-chat",
                Roles = new List<string> { "Ocr", "Solve" },
            });
        }

        foreach (var ep in Endpoints)
        {
            ep.Roles ??= new List<string>();
            if (string.IsNullOrWhiteSpace(ep.Name)) ep.Name = "未命名";
        }
    }
}

public static class AppPaths
{
    private static string? _dataDir;
    private static string? _logDir;
    private static readonly object _lock = new();

    /// <summary>
    /// 数据目录。优先放在 EXE 同目录下的 data\ —— 绿色软件，看得见、拷走就带走、删了就是清空。
    /// 只有当 EXE 所在位置不可写（比如装在 Program Files、或 U 盘只读）时，
    /// 才降级到 LocalAppData → 漫游 AppData → TEMP。
    /// 每个候选位置都会真写一个探针文件验证，避免"目录存在但只读"踩坑。
    /// </summary>
    public static string DataDir
    {
        get
        {
            if (_dataDir is not null) return _dataDir;
            lock (_lock)
            {
                if (_dataDir is not null) return _dataDir;

                var candidates = new List<string>();

                // 1) 首选：EXE 根目录\data
                try { candidates.Add(Path.Combine(AppDir, "data")); } catch { }

                // 2) 降级：用户目录
                void Add(Environment.SpecialFolder f)
                {
                    try
                    {
                        var p = Environment.GetFolderPath(f);
                        if (!string.IsNullOrWhiteSpace(p))
                            candidates.Add(Path.Combine(p, "AdbSolver"));
                    }
                    catch { }
                }

                Add(Environment.SpecialFolder.LocalApplicationData);
                Add(Environment.SpecialFolder.ApplicationData);

                // 3) 最后兜底：TEMP
                try { candidates.Add(Path.Combine(Path.GetTempPath(), "AdbSolver")); } catch { }

                foreach (var c in candidates)
                {
                    try
                    {
                        Directory.CreateDirectory(c);
                        // 真正验证可写，避免目录存在但只读
                        var probe = Path.Combine(c, ".write-probe");
                        File.WriteAllText(probe, "ok");
                        File.Delete(probe);
                        _dataDir = c;
                        return c;
                    }
                    catch { }
                }

                // 全部失败时退回当前目录，至少不抛异常
                _dataDir = AppContext.BaseDirectory;
                return _dataDir;
            }
        }
    }

    public static string LogDir
    {
        get
        {
            if (_logDir is not null) return _logDir;
            try
            {
                var dir = Path.Combine(DataDir, "logs");
                Directory.CreateDirectory(dir);
                _logDir = dir;
            }
            catch
            {
                _logDir = DataDir;
            }
            return _logDir;
        }
    }

    /// <summary>
    /// 给 adb 子进程用的临时目录。
    /// adb server 启动时会往 %TEMP%\adb.log 写日志，系统 TEMP 被锁时它会直接
    /// 报 "cannot open '...\Temp\adb.log': Permission denied" 然后起不来，
    /// 表现就是"设备列表空的 / 连不上手机"。
    /// 这里给它一个我们自己保证可写的目录。
    /// </summary>
    public static string TempDir
    {
        get
        {
            try
            {
                var dir = Path.Combine(DataDir, "tmp");
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                return DataDir;
            }
        }
    }

    public static string AppDir =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
}
