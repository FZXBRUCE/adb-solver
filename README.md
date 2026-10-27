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
| **不闪退** | 数据目录多级降级（LocalAppData → Roaming → EXE 同目录 → TEMP），任何路径不可写都不崩 |
| **全局异常兜底** | 未捕获异常写日志 + 弹窗提示，而不是静默退出 |
| **三种截图监听** | Observer（低延迟）/ Poll（最稳）/ Hybrid（双保险） |
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

程序提供三种模式：

**Observer 模式**
用 `adb shell content watch --uri content://media/external/images/media` 监听媒体库变更事件，延迟最低。缺点是部分定制 ROM（MIUI、ColorOS 等）会限制该命令。

**Poll 模式**
定时查询 MediaStore 里最新一条图片记录，发现 `date_added` 变化就抓图。兼容性最好，任何机型都能用。

**Hybrid 模式（默认推荐）**
两个同时跑，谁先触发算谁的。Observer 快、Poll 兜底，实际体验最好。

抓到图之后流程是：

```
检测到新图 → 等文件落盘（可配置延时）→ adb exec-out screencap 拉原图
→ 转 base64 → 发给 OCR 端点 → 拿到题干 → 检索 → 作答 → 显示 + 复制
```

---

## 目录结构

```
adb-solver/
├── src/AdbSolver/
│   ├── Adb/
│   │   ├── AdbClient.cs          ADB 命令封装（设备枚举/WiFi/截图）
│   │   ├── DeviceInfo.cs         设备信息模型
│   │   └── ScreenshotWatcher.cs  截图监听（Observer/Poll/Hybrid）
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

# 打包单文件 EXE（自包含，约 63MB）
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none \
  -o ../../dist/win-x64
```

产物：`dist/win-x64/AdbSolver.exe`，双击即用。

### 自检模式

不开窗口，快速验证 adb 和配置是否正常：

```bash
AdbSolver.exe --selftest
```

结果同时打印到控制台并写入 `data/selftest.txt`。

---

## 配置文件

位置优先级（第一个可写的胜出）：

1. `%LOCALAPPDATA%\AdbSolver\config.json`
2. `%APPDATA%\AdbSolver\config.json`
3. `<EXE 目录>\data\config.json`
4. `%TEMP%\AdbSolver\config.json`

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

跑 `AdbSolver.exe --selftest`，看控制台输出和 `data/logs/` 下的日志。绝大多数情况是 adb 没找到，或者数据目录不可写（已做多级降级，理论上不该发生）。

**Q: 杀毒软件报毒**

单文件自解压 EXE 容易误报。要么加白名单，要么用框架依赖版本自己编译。源码全在这里，可以自己审。

**Q: 手机连上了但显示"未授权"**

手机上没点允许。拔了重插，看到弹窗勾选"一律允许"。

**Q: 截图监听不触发**

换成 Poll 模式试试。部分 ROM 的 `content watch` 被阉割了。

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
