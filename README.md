# AdbSolver

> 手机截个图，电脑自己把题做了。

通过 ADB 连接安卓手机，实时监听系统截图，自动 OCR 识别题干、联网检索、调用大模型作答，答案直接显示在电脑上并复制到剪贴板。

**单文件 EXE，绿色免安装，换台电脑也不闪退。**

---

## 它解决什么问题

手机上刷题、答题、问卷，来回复制粘贴太麻烦。这个工具让整个流程变成：

1. 手机上截个图
2. 电脑自动识别题目
3. 自动搜索 / 自动作答
4. 答案出现在屏幕上，同时进了剪贴板

全程不用碰键盘。

---

## 核心特性

| 特性 | 说明 |
| --- | --- |
| **零依赖单文件** | `AdbSolver.exe` 自带 .NET 运行时，目标机不需要装任何东西 |
| **不闪退** | 单文件**不解压到 TEMP**（托管库和原生库直接从 EXE 映像映射），对目标机 TEMP 权限零依赖 |
| **绿色便携** | 数据就放 EXE 旁边的 `data\` 目录，看得见、拷走就带走、删了就是清空 |
| **全局异常兜底** | 未捕获异常写日志 + 弹窗提示，而不是静默退出 |
| **双通道截图监听** | MediaStore 查询 + 目录扫描，两条独立通道互相兜底 |
| **监听与答题解耦** | 没配 AI 也能正常监听抓图，不会因为缺配置就假装在工作 |
| **多 AI 端点** | OpenAI 兼容、Claude、Gemini 三种协议，支持故障转移 |
| **角色分工** | 可以指定 A 端点专门识图、B 端点专门作答 |
| **两段式答题** | 先检索再作答，正确率更高 |
| **USB / WiFi** | 支持 USB 直连和无线调试，可记住多个 WiFi 目标自动重连 |

---

## 快速开始

### 1. 准备 ADB

三种方式任选其一：

- 把 `platform-tools` 整个文件夹放到 `AdbSolver.exe` 同目录
- 把 `adb.exe` 加到系统 `PATH`
- 在「设置 → ADB」里手动指定 `adb.exe` 完整路径

> 程序会自动探测：EXE 同目录 → PATH → `C:\adb`、`D:\platform-tools`、`C:\platform-tools` 等常见位置。

### 2. 打开手机调试

1. 设置 → 关于手机 → 连点「版本号」7 次，开启开发者选项
2. 开发者选项 → 打开「USB 调试」
3. 数据线连电脑，手机上弹出「允许 USB 调试吗」→ 勾选「一律允许」→ 确定

无线调试（Android 11+）：开发者选项 → 无线调试 → 使用配对码配对设备，然后在软件里填 `IP:端口` 点连接。

### 3. 配置 AI 端点

点「设置 → AI 端点 → 打开端点配置」，添加至少一个端点：

```
名称    : DeepSeek
协议    : OpenAI 兼容
BaseURL : https://api.deepseek.com
API Key : sk-xxxxxxxx
模型    : deepseek-chat
角色    : Search, Solve
```

> **识图（OCR）必须用多模态模型**：`gpt-4o`、`claude-sonnet-4`、`gemini-2.0-flash`、`qwen-vl-max`、`glm-4v` 等。
> 纯文本模型（如 `deepseek-chat`）只能做 Search 和 Solve，不要勾 OCR。

### 4. 开始监听

选好设备 → 点「开始监听」→ 手机上随便截个图 → 看右侧出答案。

---

## 支持的 AI 协议

### OpenAI 兼容

覆盖绝大多数服务，包括中转站：

- DeepSeek：`https://api.deepseek.com`
- 月之暗面 Kimi：`https://api.moonshot.cn/v1`
- 通义千问：`https://dashscope.aliyuncs.com/compatible-mode/v1`
- 智谱 GLM：`https://open.bigmodel.cn/api/paas/v4`
- OneAPI / NewAPI / 各类中转：填你自己的地址

### Anthropic Claude 原生

`https://api.anthropic.com`，模型如 `claude-sonnet-4-20250514`

### Google Gemini 原生

`https://generativelanguage.googleapis.com`，模型如 `gemini-2.0-flash`

---

## 截图监听原理

程序内部跑两条**互相独立、互相兜底**的检测通道，任意一条命中就抓图：

**通道一：MediaStore 轮询**
```
adb shell content query --uri content://media/external/images/media \
  --projection _id:date_added:_data --sort 'date_added DESC'
```
拿最新一条图片记录的 `_id` 和 `date_added`，变了就是新图。
注意：`--sort` 只接受排序表达式，**不能塞 LIMIT**，否则设备端直接抛
`IllegalArgumentException: Invalid token LIMIT`；而且整条 shell 命令必须拼成
一个字符串传给 adb，拆成多个参数会被 adb 当成独立参数转发，设备端只收到
一个没有值的 `--sort`，直接打 usage 退出。

**通道二：目录扫描**
```
adb shell ls -t /sdcard/Pictures/Screenshots | head -20
```
再 `stat -c '%Y %s'` 比对 mtime。MediaStore 没索引、provider 被 ROM 限制、
或者刚截图还没入库的时候，这条通道兜住。实测在 vivo / ColorOS 这类机器上，
**目录扫描往往比 MediaStore 先命中**。

抓到图之后流程是：

```
检测到新图 → 等文件落盘（可配置延时）→ 优先 pull 设备上的原始截图文件
→ 拿不到路径才回退 adb exec-out screencap 现场截屏
→ SHA256 内容去重（两条通道同时命中同一张只处理一次）
→ 转 base64 → 发给 OCR 端点 → 拿到题干 → 检索 → 作答 → 显示 + 复制
```

> **重要**：截图监听和 AI 答题是解耦的。
> 就算一个 AI 端点都没配，点「开始」也会正常监听并抓图，状态栏和日志会显示
> "已抓到截图（未配 AI）"。以前这两个是绑死的，没配 AI 点开始直接弹框，
> 监听器根本没启动——你会以为监听坏了，其实它压根没在跑。

---

## 目录结构

```
adb-solver/
├── src/AdbSolver/
│   ├── Adb/
│   │   ├── AdbClient.cs          ADB 命令封装（设备枚举/WiFi/截图）
│   │   ├── DeviceInfo.cs         设备信息模型
│   │   └── ScreenshotWatcher.cs  截图监听（MediaStore + 目录扫描双通道）
│   ├── Ai/
│   │   └── AiClient.cs           三种协议统一封装 + 故障转移
│   ├── Core/
│   │   ├── AppConfig.cs          配置模型 + 数据目录多级降级
│   │   ├── Log.cs                日志
│   │   └── ProcRunner.cs         子进程执行（带超时）
│   ├── Pipeline/
│   │   ├── Prompts.cs            提示词模板
│   │   └── SolvePipeline.cs      OCR → 检索 → 作答 主流程
│   ├── UI/
│   │   ├── MainForm.cs           主窗口
│   │   ├── SettingsForm.cs       设置窗口
│   │   └── EndpointEditorForm.cs 端点编辑器
│   ├── Program.cs                入口 + 全局异常兜底
│   └── AdbSolver.csproj
├── .github/workflows/build.yml   CI：自动构建并发布 EXE
├── docs/                         文档
└── README.md
```

---

## 自己编译

需要 .NET 8 SDK。

```bash
git clone https://github.com/<your-name>/adb-solver.git
cd adb-solver/src/AdbSolver

# 调试运行
dotnet run

# 打包单文件 EXE（自包含，约 130MB）
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=false \
  -p:EnableCompressionInSingleFile=false \
  -p:DebugType=none \
  -o ../../publish
```

产物：`publish/AdbSolver.exe`，**就这一个文件**，双击即用。

> ### 为什么不能开 `IncludeNativeLibrariesForSelfExtract` / `EnableCompressionInSingleFile`
>
> 这两个开关会让单文件 EXE 在启动时**先把自己解压到 `%TEMP%\.net\<AppName>\<hash>\`**
> 才能跑起来。一旦目标机器的 TEMP 目录被以下任一机制限制，程序会直接崩在启动阶段，
> **连窗口都不弹**，只会弹一个：
>
> ```
> Failure processing application bundle.
> Failed to create directory [C:\Users\...\Temp\.net\AdbSolver\xxxx] for extracting bundled files.
> ```
>
> 常见的限制来源：
>
> - AppLocker / WDAC 软件限制策略把 `%TEMP%` 设为不可执行或不可写
> - Defender ASR 规则「阻止从 PSExec 和 WMI 创建的进程」「阻止 Office 创建子进程」的连带限制
> - 企业组策略把 `%TEMP%` 重定向到无写权限的网络路径
> - 第三方安全软件 / 沙箱 / 还原卡锁死 `%TEMP%`
> - 精简版系统把 `Temp` 目录的 ACL 改坏了
>
> 这就是「**自己电脑上好好的，拷到别人电脑双击就闪退**」的头号元凶。
> 关掉这两个开关后，托管程序集和原生库都直接从 EXE 映像里映射加载，
> **完全不落地解压**，对 TEMP 权限零依赖。代价是体积从 63MB 涨到 130MB，
> 换来的是真的到哪都能跑。
>
> 另外 `Application.EnableVisualStyles()` 内部也会往 `%TEMP%` 写一个
> activation context 清单文件，同样会因 TEMP 不可写而抛
> `UnauthorizedAccessException: Access to the path 'C:\...\Temp\xxxx.tmp' is denied`。
> `Program.cs` 里已经把它包在 try/catch 里 —— 视觉样式要不到就算了，绝不能挡住启动。
>
> ### 还有一个坑：adb server 也依赖 `%TEMP%`
>
> 程序自己不掉 TEMP 了，但它拉起来的 `adb.exe` 还在用系统 TEMP：
>
> ```
> error: cannot open 'C:\Users\xxx\AppData\Local\Temp\adb.log': Permission denied
> * failed to start daemon
> error: cannot connect to daemon
> ```
>
> adb server 起不来，**设备列表就永远是空的**，看起来像"连不上手机"。
>
> 现在 `AdbClient` 给所有 adb 子进程注入了 `TEMP` / `TMP` 环境变量，
> 指向 `<数据目录>\tmp`（保证可写），这个坑也一并堵上了。

### 自检模式

不开窗口，快速验证 adb 和配置是否正常：

```bash
AdbSolver.exe --selftest
```

结果同时打印到控制台并写入 `data/selftest.txt`。

### 诊断模式（截图抓不到时用这个）

```bash
AdbSolver.exe --probe
```

不启动界面，把截图检测链路的每一步原始返回全打出来：adb 路径、设备列表、
MediaStore 查询结果、目录扫描结果、pull 测试、以及 25 秒实时监听窗口。
这 25 秒内你在手机上截一张图，正常的话会立刻打出 `★ 命中: <本地路径>`。

结果同时写入 `data/probe.txt`，方便贴给别人看。

---

## 配置文件

**绿色软件，数据就在 EXE 旁边的 `data\` 目录里：**

```
AdbSolver.exe
data/
├── config.json          配置（含 API Key）
├── logs/                运行日志
└── captures/            抓到的截图（keepImages 打开时才留）
```

想清空重来？直接把 `data` 目录删了。想整个搬走？EXE + `data` 一起拷走就行。

只有当 EXE 所在位置**不可写**时（装在 `Program Files`、只读 U 盘、光盘），
才会自动降级到 `%LOCALAPPDATA%\AdbSolver` → `%APPDATA%\AdbSolver` → `%TEMP%\AdbSolver`。
启动日志第一行会打印实际用的数据目录，一看就知道。

格式：

```json
{
  "adb": {
    "adbPath": "",
    "commandTimeoutMs": 20000,
    "autoConnectWifi": false,
    "wifiTargets": []
  },
  "capture": {
    "mode": 2,
    "pollIntervalMs": 1200,
    "settleDelayMs": 350,
    "keepImages": false
  },
  "endpoints": [
    {
      "name": "DeepSeek",
      "kind": 0,
      "baseUrl": "https://api.deepseek.com",
      "apiKey": "sk-xxx",
      "model": "deepseek-chat",
      "roles": ["Search", "Solve"],
      "enabled": true,
      "timeoutSeconds": 120
    }
  ],
  "pipeline": {
    "enableSearch": false,
    "minQuestionChars": 6,
    "maxConsecutiveFailures": 3,
    "copyAnswerToClipboard": true,
    "answerStyle": "简洁直接，先给答案再给一句解释。"
  }
}
```

> API Key 明文存在本地配置文件里。介意的话用系统环境变量那套自己改，代码在 `Core/AppConfig.cs`。

---

## 常见问题

**Q: 双击没反应 / 一闪而过**

跑 `AdbSolver.exe --selftest`，看控制台输出和 `data\logs\` 下的日志。

如果连 `--selftest` 都报这个：

```
Failure processing application bundle.
Failed to create directory [...] for extracting bundled files.
```

说明你拿的是**旧版**（开了自解压的）。换成现在这个不开自解压的版本，就不会再出现。
如果还报 `Access to the path '...\Temp\xxxx.tmp' is denied`，那是 `EnableVisualStyles`
被 TEMP 权限挡了 —— 新版本已包了 try/catch，不会再崩。

**Q: 杀毒软件报毒**

单文件自包含 EXE 容易误报。要么加白名单，要么用框架依赖版本自己编译。源码全在这里，可以自己审。

**Q: 手机连上了但显示"未授权"**

手机上没点允许。拔了重插，看到弹窗勾选"一律允许"。

**Q: 截图监听不触发**

按顺序排查，别瞎猜：

1. **先确认它到底在不在听**。点「开始」之后状态栏应该出现
   `截图监听已启动` → `监听就绪（当前最新截图: xxx.jpg）`。
   如果没有这两行，说明监听器压根没启动。
2. 跑诊断模式，它会把每一步的原始返回全打出来：
   ```
   AdbSolver.exe --probe
   ```
   输出里 `[1] 媒体库` / `[2] 目录扫描` 应该都能拿到文件路径；
   `[4] 实时监听 25 秒` 期间你在手机上截一张图，应该出现 `★ 命中: ...`。
3. 看日志 `data/logs/adbsolver-*.log`，搜索 `基线` 和 `命中`。
4. 截图目录不在默认位置的机型（部分 ROM 存到
   `/sdcard/DCIM/Screenshots` 或 `/sdcard/Pictures` 根下），
   程序会把这几个目录都扫一遍，不用手动配。
5. 如果 `[1]` 空但 `[2]` 有结果——正常，说明 MediaStore 没索引，
   目录扫描在兜底，功能不受影响。

**Q: 抓到的图是旧的**

`轮询间隔` 调小一点（默认 700ms，可以到 200ms）。
另外 `落盘延时` 别设太大，截图刚生成时文件可能还没写完。

**Q: OCR 出来是乱码**

模型不支持识图。检查端点配置里角色有没有勾 OCR，以及模型是不是多模态的。

**Q: 答案不准确**

1. 打开「先检索再作答」
2. 换个更强的模型
3. 在「作答风格」里写清楚你要什么格式

---

## 免责声明

本项目仅供学习 ADB 自动化、OCR 与大模型 API 集成等技术使用。

使用者应自行确保其使用场景符合当地法律法规及目标平台的服务条款。作者不对任何滥用行为负责。

---

## License

MIT
