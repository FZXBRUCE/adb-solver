# 建库信息（复制粘贴用）

## 1. 仓库名

```
adb-solver
```

## 2. Description（GitHub 建库那个输入框，限 350 字符）

**推荐这个（中文，118 字符）：**

```
通过 ADB 监听安卓手机截图，自动 OCR 识别题目、联网检索、调用大模型作答。单文件 EXE，绿色免安装，不依赖 .NET 运行时，换台电脑也不闪退。
```

**英文版（面向国际用户，142 字符）：**

```
Monitor Android screenshots via ADB, auto-OCR the question, search the web, and answer with an LLM. Single-file EXE, portable, no .NET runtime required.
```

## 3. Topics（建完库后在 About 右侧齿轮里加）

```
adb
android
screenshot
ocr
llm
openai
claude
gemini
winforms
dotnet
windows
automation
single-file
portable
```

一次最多加 20 个，上面 14 个够用。

## 4. 建库时怎么勾

- **Public**（要别人能下 Release 就必须 Public）
- **不要**勾 Add a README / .gitignore / license —— 仓库里已经有了，勾了会冲突，push 时要处理
- 直接建空仓库

## 5. 建完执行

```bash
cd D:\dsh\projects\adb-solver
git remote add origin https://github.com/<你的用户名>/adb-solver.git
git branch -M main
git push -u origin main

# 打 tag 触发 CI 自动发 Release
git tag v1.0.0
git push origin v1.0.0
```

---

# 备用：如果想要的是一句话简介

- 手机截个图，电脑自己把题做了。
- 截图 → 识别 → 检索 → 作答，全程不用碰键盘。
- 安卓截图自动答题工具，单文件绿色版。
- 你负责截图，剩下的交给它。
