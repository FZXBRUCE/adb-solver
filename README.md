# AdbSolver

手机截个图，电脑把题做出来。

通过 ADB 连接安卓手机，监听系统截图目录，自动识别题干、调用大模型作答，答案显示在电脑上并复制到剪贴板。

单文件 EXE，绿色免安装。

---

## 干什么用的

手机上刷题、答题、填问卷，来回复制粘贴麻烦。这工具把流程压成三步：

1. 手机上截个图
2. 电脑自动识别题目
3. 答案出现在屏幕上，同时进了剪贴板

---

## 功能

| 功能 | 说明 |
| --- | --- |
| 单文件 | `AdbSolver.exe` 自带 .NET 运行时，目标机什么都不用装 |
| 不解压 TEMP | 托管库和原生库直接从 EXE 映像映射，对目标机 TEMP 权限零依赖，不会出现启动即崩 |
| 便携 | 数据放 EXE 旁边的 `data\` 目录，删了就是清空 |
| 异常兜底 | 未捕获异常写日志并弹窗，不静默退出 |
| 双通道监听 | MediaStore 查询 + 目录扫描，两条独立通道互相兜底 |
| 监听与答题解耦 | 没配 AI 也能监听抓图 |
| 多端点 | OpenAI 兼容、Claude、Gemini 三种协议，支持故障转移 |
| 角色分工 | 可以指定 A 端点识图、B 端点作答 |
| USB / WiFi | 支持 USB 直连和无线调试，可记住多个 WiFi 目标自动重连 |
| 反检测 | 可选开关，默认关闭，见下文 |

---

## 下载

不用编译，不用装 .NET，不用克隆仓库。

去 [Releases](https://github.com/FZXBRUCE/adb-solver/releases) 下载最新的 `AdbSolver.exe`，双击运行。就这一个文件。卸载直接删，不留注册表。

仓库里的源码是给要改代码、要自己编译的人看的。

---

## 快速开始

### 1. 准备 ADB

三种方式任选：

- 把 `platform-tools` 整个文件夹放到 `AdbSolver.exe` 同目录
- 把 `adb.exe` 加进系统 `PATH`
- 在「设置 → ADB」里手动指定 `adb.exe` 路径

程序自动探测顺序：EXE 同目录 → PATH → `C:\adb`、`D:\platform-tools`、`C:\platform-tools` 等常见位置。

### 2. 打开手机调试

1. 设置 → 关于手机 → 连点「版本号」7 次
2. 开发者选项 → 打开「USB 调试」
3. 数据线连电脑，手机弹窗选「一律允许」→ 确定

无线调试（Android 11 以上）：开发者选项 → 无线调试 → 使用配对码配对，然后在软件里填 `IP:端口` 点连接。

### 3. 配置 AI 端点

点「设置 → AI 端点 → 打开端点配置」，至少加一个：

```
名称    : DeepSeek
协议    : OpenAI 兼容
BaseURL : https://api.deepseek.com
API Key : sk-xxxxxxxx
模型    : deepseek-chat
角色    : Solve
```

识图必须用多模态模型：`gpt-4o`、`claude-sonnet-4`、`gemini-2.0-flash`、`qwen-vl-max`、`glm-4v` 这类。纯文本模型只能做 Solve，角色里别勾 Ocr。

模型名别手敲。填完 BaseURL 和 API Key 后点「模型」右边的「获取模型列表」，程序直接向服务端要清单，下拉选就行。中转站的模型名跟官方不一样，同一个模型可能叫 `claude-sonnet-4`、`anthropic/claude-sonnet-4` 或者站主自定义的别名，手敲大概率被拒，报错还看不出正确名字。少数站没实现 `/models` 接口，那种才需要手敲（下拉框也允许直接输入）。

### 4. 开始监听

选设备 → 点「开始监听」→ 手机上截个图 → 右侧出答案。

---

## 支持的 AI 协议

### OpenAI 兼容

- DeepSeek：`https://api.deepseek.com`
- 月之暗面 Kimi：`https://api.moonshot.cn/v1`
- 通义千问：`https://dashscope.aliyuncs.com/compatible-mode/v1`
- 智谱 GLM：`https://open.bigmodel.cn/api/paas/v4`
- OneAPI / NewAPI / 各类中转：填自己的地址

中转站记得先点「获取模型列表」，模型名是站主自己定的。

### Anthropic Claude 原生

`https://api.anthropic.com`，模型如 `claude-sonnet-4-20250514`

### Google Gemini 原生

`https://generativelanguage.googleapis.com`，模型如 `gemini-2.0-flash`

---

## 截图监听原理

程序跑两条独立通道，任意一条命中就抓图。

**通道一：MediaStore 轮询**

```
adb shell content query --uri content://media/external/images/media \
  --projection _id:date_added:_data --sort 'date_added DESC'
```

取最新一条图片记录的 `_id` 和 `date_added`，变了就是新图。

两个坑：`--sort` 只接受排序表达式，塞 LIMIT 会让设备端抛 `IllegalArgumentException: Invalid token LIMIT`；整条 shell 命令必须拼成一个字符串传给 adb，拆成多个参数会被当成独立参数转发，设备端只收到一个没有值的 `--sort`，打 usage 退出。

**通道二：目录扫描**

```
adb shell ls -t /sdcard/Pictures/Screenshots | head -20
```

再用 `stat -c '%Y %s'` 比对 mtime。MediaStore 没索引、provider 被 ROM 限制、或者刚截图还没入库时靠这条兜底。在 vivo、ColorOS 这类机器上实测，目录扫描往往比 MediaStore 先命中。

抓到图之后的流程：

```
检测到新图 → 等文件落盘 → 优先 pull 设备上的原始截图
→ 拿不到路径才回退 adb exec-out screencap 现场截屏
→ SHA256 去重（两条通道命中同一张只处理一次）
→ 转 base64 → 发 OCR 端点 → 拿题干 → 作答 → 显示 + 复制
```

截图监听和 AI 答题是解耦的。一个端点都没配，点「开始」也会正常监听抓图，状态栏显示「已抓到截图（未配 AI）」。早期版本这两件事绑在一起，没配 AI 点开始直接弹框，监听器压根没启动。

---

## 反检测开关

设置里有「反检测」页，默认关闭。关着的时候程序行为和以前完全一样。

开启后改变四件事：

| 项目 | 关 | 开 |
|---|---|---|
| 抓图方式 | `screencap` 落 `/sdcard/adbsolver_cap.png` 再 `pull` | `exec-out screencap -p` 直接在内存里拿 PNG，设备不留文件 |
| 临时文件名 | 固定 `adbsolver_cap.png` | `.adbsc_<随机>.png`，点开头是隐藏文件 |
| 临时目录 | `/sdcard`，任何 App 都能 ls | `/data/local/tmp`，普通 App 读不到 |
| 轮询节奏 | 固定 250ms | ±30% 随机抖动 |
| 窗口标题 | AdbSolver · ADB 截图自动答题 | 截图工具，可自定义 |

改不了的：系统设置里「USB 调试」的开关状态、5037 端口是否在监听、App 设了 `FLAG_SECURE` 导致截图是黑图。这三样在手机侧，PC 端无法处理。

这开关只解决设备上的文件痕迹、PC 端的进程痕迹、和机器化的操作节奏。真要藏得深，还得配合改 adb 客户端名称、用无线调试、别在同一台机器上长时间挂机。

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
│   │   └── SolvePipeline.cs      识别 → 作答 主流程
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
git clone https://github.com/FZXBRUCE/adb-solver.git
cd adb-solver

dotnet publish src/AdbSolver/AdbSolver.csproj -c Release -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:DebugType=none \
  -o publish
```

调试运行：

```bash
cd src/AdbSolver
dotnet run
```

产物 `publish/AdbSolver.exe`，就这一个文件。

### 为什么不能开 IncludeNativeLibrariesForSelfExtract 和 EnableCompressionInSingleFile

这两个开关会让单文件 EXE 启动前先把自身解压到 `%TEMP%\.net\<AppName>\<hash>\`。目标机 TEMP 一旦受限，程序会崩在启动阶段，窗口都不弹，只报：

```
Failure processing application bundle.
Failed to create directory [C:\Users\...\Temp\.net\AdbSolver\xxxx] for extracting bundled files.
```

常见的 TEMP 限制来源：AppLocker / WDAC 策略把 `%TEMP%` 设为不可执行或不可写；Defender ASR 规则的连带限制；企业组策略把 `%TEMP%` 重定向到无写权限的网络路径；第三方安全软件、沙箱、还原卡锁死 `%TEMP%`；精简版系统把 `Temp` 目录 ACL 改坏。

这是「自己电脑上好好的，拷到别人电脑双击就闪退」最常见的原因。关掉这两个开关后，托管程序集和原生库直接从 EXE 映像映射加载，不落地解压，对 TEMP 权限零依赖。代价是体积从 63MB 涨到 130MB。

另一个相关位置：`Application.EnableVisualStyles()` 内部会往 `%TEMP%` 写一个 activation context 清单文件，同样会因 TEMP 不可写抛 `UnauthorizedAccessException`。`Program.cs` 里已包 try/catch。

### adb server 也依赖 TEMP

程序自己不落 TEMP 了，但它拉起来的 `adb.exe` 还在用系统 TEMP：

```
error: cannot open 'C:\Users\xxx\AppData\Local\Temp\adb.log': Permission denied
* failed to start daemon
error: cannot connect to daemon
```

adb server 起不来，设备列表就永远是空的，看起来像连不上手机。

`AdbClient` 给所有 adb 子进程注入了 `TEMP` / `TMP` 环境变量，指向 `<数据目录>\tmp`，这个坑一并堵上。

### 自检模式

不开窗口，验证 adb 和配置：

```bash
AdbSolver.exe --selftest
```

结果打印到控制台并写入 `data/selftest.txt`。

### 诊断模式

截图抓不到时用这个：

```bash
AdbSolver.exe --probe
```

不启动界面，把截图检测链路每一步的原始返回全打出来：adb 路径、设备列表、MediaStore 查询、目录扫描、pull 测试、以及 25 秒实时监听窗口。这 25 秒内截一张图，正常会打出 `★ 命中: <本地路径>`。

结果同时写入 `data/probe.txt`。

---

## 配置文件

数据在 EXE 旁边的 `data\` 目录：

```
AdbSolver.exe
data/
├── config.json          配置（含 API Key）
├── logs/                运行日志
└── captures/            抓到的截图（keepImages 打开时才留）
```

清空重来就删 `data` 目录。整体搬走就把 EXE 和 `data` 一起拷。

只有 EXE 所在位置不可写时（装在 `Program Files`、只读 U 盘、光盘），才降级到 `%LOCALAPPDATA%\AdbSolver` → `%APPDATA%\AdbSolver` → `%TEMP%\AdbSolver`。启动日志第一行会打印实际用的目录。

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
    "pollIntervalMs": 250,
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
      "roles": ["Solve"],
      "enabled": true
    }
  ],
  "pipeline": {
    "minQuestionChars": 8,
    "maxConsecutiveFailures": 5,
    "copyAnswerToClipboard": true,
    "answerStyle": "简洁直接，先给答案再给一句解释。"
  },
  "stealth": {
    "enabled": false,
    "captureInMemory": true,
    "remoteTempDir": "/data/local/tmp",
    "randomizeTempNames": true,
    "pollJitter": 0.3,
    "discreetTitle": true,
    "windowTitle": ""
  }
}
```

API Key 明文存在本地配置文件里。介意就自己改成从环境变量读，代码在 `Core/AppConfig.cs`。

---

## 常见问题

**双击没反应 / 一闪而过**

跑 `AdbSolver.exe --selftest`，看控制台输出和 `data\logs\` 下的日志。

如果连 `--selftest` 都报：

```
Failure processing application bundle.
Failed to create directory [...] for extracting bundled files.
```

说明拿的是旧版（开了自解压的）。换现在这个不开自解压的版本就不会再出现。如果报 `Access to the path '...\Temp\xxxx.tmp' is denied`，那是 `EnableVisualStyles` 被 TEMP 权限挡了，新版本已包 try/catch。

**杀毒软件报毒**

单文件自包含 EXE 容易误报。加白名单，或者自己编译框架依赖版本。源码全在仓库里。

**手机连上了但显示「未授权」**

手机上没点允许。拔了重插，弹窗勾选「一律允许」。

**截图监听不触发**

按顺序排查：

1. 点「开始」之后状态栏应该出现 `截图监听已启动` → `监听就绪（当前最新截图: xxx.jpg）`。没有这两行说明监听器没启动。
2. 跑 `AdbSolver.exe --probe`，输出里 `[1] 媒体库` / `[2] 目录扫描` 应该都能拿到文件路径；`[4] 实时监听 25 秒` 期间截一张图应该出现 `★ 命中: ...`。
3. 看 `data/logs/adbsolver-*.log`，搜 `基线` 和 `命中`。
4. 截图目录不在默认位置的机型（部分 ROM 存到 `/sdcard/DCIM/Screenshots` 或 `/sdcard/Pictures` 根下），程序会把这几个目录都扫一遍。
5. `[1]` 空但 `[2]` 有结果是正常的，MediaStore 没索引，目录扫描在兜底，功能不受影响。

**抓到的图是旧的**

轮询间隔调小一点（默认 250ms，可以到 200ms）。落盘延时别设太大，截图刚生成时文件可能还没写完。

**OCR 出来是乱码**

模型不支持识图。检查端点角色有没有勾 Ocr，以及模型是不是多模态的。

**答案不准确**

1. 换个更强的模型
2. 在「作答风格」里写清楚要什么格式

---

## 免责声明

本项目仅供学习 ADB 自动化、OCR 与大模型 API 集成使用。

使用者自行确保使用场景符合当地法律法规及目标平台服务条款。作者不对任何滥用行为负责。

---

## License

MIT
