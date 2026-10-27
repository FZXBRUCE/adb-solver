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
    /// 该端点承担哪些角色：Ocr=识图, Search=联网搜索, Solve=作答。
    /// 留空表示三个角色都能干。
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
    /// <summary>监听方式：Observer=ContentObserver 文件系统监听（快、但需要 shell 常驻）, Poll=定时轮询 MediaStore。</summary>
    public CaptureMode Mode { get; set; } = CaptureMode.Hybrid;

    /// <summary>轮询间隔（毫秒）。Hybrid 模式下作为兜底频率。</summary>
    public int PollIntervalMs { get; set; } = 700;

    /// <summary>去重时间窗（毫秒）。同一张图在这段时间内不重复处理。</summary>
    public int DedupWindowMs { get; set; } = 2500;

    /// <summary>截图后等待文件写盘完成的额外延时（毫秒）。</summary>
    public int SettleDelayMs { get; set; } = 350;

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

public sealed class PipelineConfig
{
    /// <summary>识别到的文本最少多少字符才当作有效题目。</summary>
    public int MinQuestionChars { get; set; } = 8;

    /// <summary>是否开启"先搜索再作答"两段式。关掉则一步直接作答。</summary>
    public bool EnableSearch { get; set; } = true;

    /// <summary>搜索阶段每个端点最多返回的结果条数提示。</summary>
    public int SearchResultHint { get; set; } = 5;

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

    /// <summary>补齐空目录、默认端点等，保证下游不为 null。</summary>
    public void Normalize()
    {
        Adb ??= new AdbConfig();
        Capture ??= new CaptureConfig();
        Pipeline ??= new PipelineConfig();
        Endpoints ??= new List<AiEndpointConfig>();
        Adb.WifiTargets ??= new List<string>();

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
                Roles = new List<string> { "Ocr", "Search", "Solve" },
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
    /// 数据目录。按 LocalAppData → 漫游 AppData → EXE 同目录 → TEMP 的顺序降级，
    /// 任何一个位置不可写都不会让程序崩掉。这是"换台电脑不闪退"的关键之一。
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
                try { candidates.Add(Path.Combine(AppDir, "data")); } catch { }
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

    public static string AppDir =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
}
