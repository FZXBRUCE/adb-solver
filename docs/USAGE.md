# 使用文档

## 目录

- [截图监听机制详解](#截图监听机制详解)
- [多端点配置与角色分工](#多端点配置与角色分工)
- [提示词定制](#提示词定制)
- [无线调试](#无线调试)
- [排错手册](#排错手册)

---

## 截图监听机制详解

### 为什么不用 `adb shell screencap` 死循环

因为 `screencap` 每次都把整张 PNG 编码一遍，1080p 屏幕大概 1~2 MB，一张要 200~500ms。轮询间隔再短也是在空转，手机发热、耗电、还拖慢系统。

所以本项目的策略是：**先低成本检测"有没有新截图"，命中之后才真正去拉图。**

### Observer 模式

```bash
adb shell content watch --uri content://media/external/images/media
```

`content watch` 会阻塞等待 ContentProvider 的变更通知。MediaStore 新增一条图片记录时立刻返回。

- 延迟：通常 100~300ms
- 优点：极低开销，纯事件驱动
- 缺点：部分 ROM（MIUI/ColorOS/EMUI）限制了该命令；Android 10+ 的分区存储也可能不通知

### Poll 模式

```bash
adb shell content query --uri content://media/external/images/media \
  --projection _id:date_added:_data --sort "date_added DESC" --limit 1
```

拿最新一条记录的 `date_added`，和上次比对。变了就说明有新图。

- 延迟：等于轮询间隔（默认 1200ms）
- 优点：兼容性最好，任何机型都能用
- 缺点：有固定延迟，频繁查询也有开销

### Hybrid 模式

两个任务并行跑，谁先发现谁触发，另一个的触发会被去重。

实际体验：绝大多数情况下 Observer 先命中（快），Observer 失效时 Poll 兜底（稳）。

### 落盘延时

截图刚生成时 MediaStore 记录已存在，但文件可能还没写完。直接拉会拿到半张图或者 0 字节。

所以命中后默认等 `SettleDelayMs`（350ms）再拉。如果你的手机存储慢，可以调到 600~800ms。

### 拉图方式

```bash
adb -s <serial> exec-out screencap -p
```

`exec-out` 直接把二进制流回传，不在手机上落临时文件，比 `screencap /sdcard/x.png` + `pull` 快得多，也干净。

---

## 多端点配置与角色分工

### 三种角色

| 角色 | 用途 | 模型要求 |
| --- | --- | --- |
| **OCR** | 看截图，把题目文字提取出来 | 必须多模态（能看图） |
| **Search** | 基于题干做知识检索/整理 | 纯文本即可 |
| **Solve** | 根据题干（和检索结果）给出最终答案 | 纯文本即可，越强越好 |

### 为什么要分工

- 多模态模型贵，让它只干"看图识字"这一件事最划算
- 纯文本推理模型便宜且强，用来检索和作答
- 一个端点挂了，另一个端点自动接上

### 典型组合

**省钱组合**

```
端点 A：gemini-2.0-flash    角色 OCR
端点 B：deepseek-chat       角色 Search, Solve
```

**质量优先**

```
端点 A：gpt-4o              角色 OCR
端点 B：claude-sonnet-4     角色 Search, Solve
```

**全能单端点**

```
端点 A：qwen-vl-max         角色 OCR, Search, Solve
```

### 故障转移规则

按端点列表顺序尝试，第一个返回非空结果的胜出。

失败判定：HTTP 非 2xx、超时、返回空内容、JSON 解析失败。

每个端点有独立超时（默认 120 秒），不会因为一个慢端点卡死整个流程。

---

## 提示词定制

提示词模板在 `src/AdbSolver/Pipeline/Prompts.cs`，改完重新编译即可。

### OCR 提示词

目标：只输出题干原文，不要任何解释、不要 markdown、不要"图片中显示"这种废话。

```csharp
public const string OcrSystem = """
    你是一个高精度的中文 OCR + 版面理解引擎。你的唯一任务是把图片里的文字准确转写成纯文本。

    硬性要求：
    1. 只输出图片中真实存在的文字，绝对不要补充、推测、联想、改写。
    2. 保持原有的阅读顺序和分行；题目、选项、题干编号要保留原样。
    3. 图片里如果有公式、符号、代码，尽量用等价的纯文本表示。
    4. 识别不清的字符用 ▢ 占位，不要瞎猜。
    5. 不要输出任何解释、前言、结尾、Markdown 代码块围栏。
    6. 如果图片里完全没有可读文字，只输出一个词：EMPTY
    """;
```

### Parse 提示词

OCR 结果里混着状态栏、电量、App 导航栏、广告、按钮，这一步负责把"当前要答的那道题"切出来，输出成固定结构：

```
TYPE: choice | fill | judge | short | unknown
QUESTION: <题干完整文本>
OPTIONS:
A. <内容>
...
ANSWER_FORMAT: <期望的答案形式>
```

### Solve 提示词

`SolveSystemTemplate` 里的 `{0}` 会被「设置 → 作答流程 → 作答风格」的内容替换。目标：先给答案，再给一句理由，答案要能被直接抄走。

你可以写：

- `只输出选项字母`
- `先给答案，再给两句话解释`
- `用中文回答，答案控制在 50 字内`

完整的提示词见 `src/AdbSolver/Pipeline/Prompts.cs`，改完重新编译即可。

---

## 无线调试

### Android 11 及以上（推荐）

1. 手机和电脑连同一个 WiFi
2. 开发者选项 → 无线调试 → 打开
3. 点「使用配对码配对设备」，记下 IP、端口、6 位配对码
4. 电脑上执行：

```bash
adb pair 192.168.1.100:37123
# 输入配对码

adb connect 192.168.1.100:5555
```

5. 软件里「WiFi 连接」填 `192.168.1.100:5555`，点连接

> 配对端口和连接端口不是同一个，配对完要用无线调试界面上显示的那个 `IP:端口`。

### Android 10 及以下

需要 root，或者先用 USB 连上执行：

```bash
adb tcpip 5555
adb connect 192.168.1.100:5555
# 之后可以拔掉数据线
```

### 自动重连

在「设置 → ADB」里填好 WiFi 目标，勾选「启动时自动连接」。

程序启动时会依次尝试所有目标，连上的就记住。

> 手机重启后 `adb tcpip` 会失效，需要重新用 USB 执行一次。

---

## 排错手册

### 程序起不来

```bash
AdbSolver.exe --selftest
```

看控制台输出。会打印：数据目录、配置路径、端点数量、adb 路径、adb 是否存在、设备列表。

同时写入 `data/selftest.txt`。

如果连自检都没输出，说明 EXE 本身有问题（下载不完整 / 被杀软拦截）。

### 日志在哪

```
<数据目录>\logs\adbsolver-YYYYMMDD.log
```

数据目录按顺序是：

1. `%LOCALAPPDATA%\AdbSolver`
2. `%APPDATA%\AdbSolver`
3. `<EXE 目录>\data`
4. `%TEMP%\AdbSolver`

设置窗口里点「打开数据目录」直接跳过去。

### adb 找不到

自检输出里看 `adb 路径` 那一行。

如果显示 `adb`（没有完整路径）且 `存在: False`，说明 PATH 里也没有。

解决：把 `platform-tools` 文件夹丢到 EXE 同目录，或者在设置里手动指定。

### 设备列表空的

```bash
adb devices
```

- 列表为空 → 数据线问题 / 驱动问题 / 手机没开 USB 调试
- 显示 `unauthorized` → 手机上点允许
- 显示 `offline` → `adb kill-server` 然后重来

### 截图监听没反应

1. 先点「测试截图」按钮，确认能拉到图
2. 能拉图但监听不触发 → 换 Poll 模式
3. Poll 也不触发 → 检查手机是不是把截图存到了非标准目录（部分 ROM 存 `/sdcard/Pictures/Screenshots`，MediaStore 里应该有记录）

手动验证 MediaStore：

```bash
adb shell content query --uri content://media/external/images/media \
  --projection _id:date_added:_data --sort "date_added DESC" --limit 3
```

### AI 返回 401 / 403

- API Key 错了或者过期
- BaseURL 少了 `/v1`（OpenAI 兼容协议有些服务要，有些不要，试两种）
- 账户余额不足

### AI 返回空

- 模型不支持识图（OCR 角色用了纯文本模型）
- 提示词被安全策略拦截
- 端点超时（调大 `timeoutSeconds`）

### 中文乱码

程序内部全程 UTF-8。乱码通常来自：

- 控制台代码页（`chcp 65001` 可解决）
- 旧版 Windows 的 ANSI 默认编码（不影响 GUI）

GUI 显示不受影响。

---

## 性能参考

| 场景 | 耗时 |
| --- | --- |
| Observer 检测到截图 | 100~300ms |
| Poll 检测到截图 | 0~1200ms（取决于轮询相位） |
| 拉图（1080p） | 150~400ms |
| OCR（gemini-2.0-flash） | 1~3s |
| OCR（gpt-4o） | 2~5s |
| 作答（deepseek-chat） | 2~8s |
| **端到端（不开检索）** | **约 4~12s** |
| **端到端（开检索）** | **约 8~25s** |

> 网络状况影响很大，以上是国内直连的参考值。
