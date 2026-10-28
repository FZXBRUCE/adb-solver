using AdbSolver.Adb;
using AdbSolver.Ai;
using AdbSolver.Core;

namespace AdbSolver.UI;

public sealed class SettingsForm : Form
{
    private readonly AppConfig _cfg;

    private readonly TextBox _txtAdbPath = new();
    private readonly NumericUpDown _numTimeout = new();
    private readonly ComboBox _cmbMode = new();
    private readonly NumericUpDown _numPoll = new();
    private readonly NumericUpDown _numSettle = new();
    private readonly CheckBox _chkKeep = new();
    private readonly CheckBox _chkClipboard = new();
    private readonly NumericUpDown _numMinChars = new();
    private readonly NumericUpDown _numMaxFail = new();
    private readonly TextBox _txtStyle = new();
    private readonly Label _lblEndpointCount = new();

    private readonly CheckBox _chkStealth = new();
    private readonly CheckBox _chkStealthMemory = new();
    private readonly CheckBox _chkStealthRandom = new();
    private readonly TextBox _txtStealthDir = new();
    private readonly NumericUpDown _numStealthJitter = new();
    private readonly CheckBox _chkStealthTitle = new();
    private readonly TextBox _txtStealthTitle = new();

    public SettingsForm(AppConfig cfg, AdbClient? adb)
    {
        // 拿副本改。原来直接把主窗口的配置对象丢进来就地修改，
        // 用户点「取消」甚至按 X 关掉时，改动已经落到那个对象上了，
        // 只是没写盘——下次任何一次保存就会把取消掉的改动一起带出去。
        // 现在改成：点「保存」才 CopyFrom 回去，取消就什么都不留。
        _cfg = cfg.Clone();

        Text = "设置";
        Width = 720;
        Height = 640;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9f);

        var tabs = new TabControl { Dock = DockStyle.Fill };

        // ============ ADB ============
        var pAdb = new TabPage("ADB") { Padding = new Padding(14) };
        var lAdb = NewTable();

        _txtAdbPath.Text = cfg.Adb.AdbPath;
        _txtAdbPath.Dock = DockStyle.Fill;

        var btnBrowse = new Button { Text = "浏览…", Width = 70, Height = 26, Dock = DockStyle.Right };
        btnBrowse.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog { Filter = "adb.exe|adb.exe|所有文件|*.*" };
            if (ofd.ShowDialog(this) == DialogResult.OK) _txtAdbPath.Text = ofd.FileName;
        };
        var pathRow = new Panel { Dock = DockStyle.Fill, Height = 28 };
        pathRow.Controls.Add(_txtAdbPath);
        pathRow.Controls.Add(btnBrowse);

        _numTimeout.Minimum = 3000;
        _numTimeout.Maximum = 120000;
        _numTimeout.Increment = 1000;
        _numTimeout.Value = cfg.Adb.CommandTimeoutMs;
        _numTimeout.Dock = DockStyle.Left;
        _numTimeout.Width = 110;

        AddRow(lAdb, "adb.exe 路径", pathRow, 30);
        AddRow(lAdb, "命令超时(ms)", _numTimeout, 26);

        var adbInfo = new Label
        {
            Dock = DockStyle.Top,
            Height = 60,
            ForeColor = Color.DimGray,
            Padding = new Padding(0, 10, 0, 0),
            Text = "留空则自动探测：程序目录 → PATH → C:\\adb、D:\\platform-tools 等常见位置。\n" +
                   $"当前解析结果：{adb?.AdbPath ?? "(未初始化)"}",
        };

        pAdb.Controls.Add(adbInfo);
        pAdb.Controls.Add(lAdb);

        // ============ 截图监听 ============
        var pCap = new TabPage("截图监听") { Padding = new Padding(14) };
        var lCap = NewTable();

        _cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbMode.Items.AddRange(new object[]
        {
            "标准（250ms）",
            "灵敏（150ms，更跟手）",
            "省电（1500ms，延迟更大）",
        });
        // 用轮询间隔反推当前档位
        _cmbMode.SelectedIndex = cfg.Capture.PollIntervalMs <= 200 ? 1
            : cfg.Capture.PollIntervalMs >= 1500 ? 2 : 0;
        _cmbMode.Dock = DockStyle.Left;
        _cmbMode.Width = 200;
        _cmbMode.SelectedIndexChanged += (_, _) =>
        {
            var v = _cmbMode.SelectedIndex switch { 1 => 150, 2 => 1500, _ => 250 };
            if ((int)_numPoll.Value != v) _numPoll.Value = v;
        };

        _numPoll.Minimum = 100;
        _numPoll.Maximum = 10000;
        _numPoll.Increment = 100;
        _numPoll.Value = cfg.Capture.PollIntervalMs;
        _numPoll.Dock = DockStyle.Left;
        _numPoll.Width = 110;
        _numPoll.ValueChanged += (_, _) =>
        {
            // 手动改了间隔，档位跟着联动
            var v = (int)_numPoll.Value;
            _cmbMode.SelectedIndex = v <= 200 ? 1 : v >= 1500 ? 2 : 0;
        };

        _numSettle.Minimum = 0;
        _numSettle.Maximum = 5000;
        _numSettle.Increment = 50;
        _numSettle.Value = cfg.Capture.SettleDelayMs;
        _numSettle.Dock = DockStyle.Left;
        _numSettle.Width = 110;

        _chkKeep.Text = "保留截图原图（方便排查问题）";
        _chkKeep.Checked = cfg.Capture.KeepImages;
        _chkKeep.Dock = DockStyle.Left;
        _chkKeep.Width = 280;

        AddRow(lCap, "监听方式", _cmbMode, 26);
        AddRow(lCap, "轮询间隔(ms)", _numPoll, 26);
        AddRow(lCap, "落盘延时(ms)", _numSettle, 26);
        AddRow(lCap, "图片保留", _chkKeep, 24);

        var capInfo = new Label
        {
            Dock = DockStyle.Top,
            Height = 110,
            ForeColor = Color.DimGray,
            Padding = new Padding(0, 10, 0, 0),
            Text = "说明：\n" +
                   "• 两条通道并行工作：① 查系统媒体库最新图片记录  ② 直接扫截图目录。\n" +
                   "• 任意一条发现新截图就立刻抓图，两条同时命中会自动去重。\n" +
                   "• 抓到后优先 pull 设备上的原始截图文件（就是你按截图键那一刻的画面）。\n" +
                   "• 落盘延时是截图后等待文件写完的时间，太短会拉到不完整的图。\n" +
                   "• 监听没反应时先点主界面「测试截图」确认能取到图，再看日志里的「命中」记录。",
        };

        pCap.Controls.Add(capInfo);
        pCap.Controls.Add(lCap);

        // ============ AI 端点 ============
        var pAi = new TabPage("AI 端点") { Padding = new Padding(14) };
        var aiPanel = new Panel { Dock = DockStyle.Fill };

        _lblEndpointCount.Dock = DockStyle.Top;
        _lblEndpointCount.Height = 34;
        _lblEndpointCount.Text = $"已配置 {_cfg.Endpoints.Count} 个端点，启用 {_cfg.Endpoints.Count(e => e.Enabled)} 个";

        var btnEdit = new Button
        {
            Text = "打开端点配置…",
            Dock = DockStyle.Top,
            Height = 40,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        btnEdit.FlatAppearance.BorderSize = 0;
        btnEdit.Click += (_, _) =>
        {
            using var dlg = new EndpointEditorForm(_cfg.Endpoints);
            dlg.ShowDialog(this);
            _cfg.Endpoints = dlg.Result;
            _lblEndpointCount.Text =
                $"已配置 {_cfg.Endpoints.Count} 个端点，启用 {_cfg.Endpoints.Count(e => e.Enabled)} 个";
        };

        var aiInfo = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            Padding = new Padding(0, 12, 0, 0),
            Text = "支持的协议：\n" +
                   "  • OpenAI 兼容 —— DeepSeek、Kimi、通义千问、智谱、OneAPI / NewAPI、各类中转站\n" +
                   "  • Anthropic Claude 原生协议\n" +
                   "  • Google Gemini 原生协议\n\n" +
                   "故障转移：按列表顺序尝试，第一个成功返回的胜出。\n" +
                   "角色分工：可让 A 端点专门识图、B 端点专门作答，留空则全部承担。\n\n" +
                   "识图（OCR）需要多模态模型，例如 gpt-4o / claude-sonnet / gemini-2.x / qwen-vl-max / glm-4v。\n" +
                   "纯文本模型（deepseek-chat 等）可以专门承担 Solve。整条链路只有两个阶段：识图切题（需视觉）+ 作答（文本即可）。",
        };

        aiPanel.Controls.Add(aiInfo);
        aiPanel.Controls.Add(btnEdit);
        aiPanel.Controls.Add(_lblEndpointCount);
        pAi.Controls.Add(aiPanel);

        // ============ 流水线 ============
        var pPipe = new TabPage("答题流程") { Padding = new Padding(14) };
        var lPipe = NewTable();

        _chkClipboard.Text = "答案自动复制到剪贴板";
        _chkClipboard.Checked = cfg.Pipeline.CopyAnswerToClipboard;
        _chkClipboard.Dock = DockStyle.Left;
        _chkClipboard.Width = 300;

        _numMinChars.Minimum = 1;
        _numMinChars.Maximum = 200;
        _numMinChars.Value = Math.Clamp(cfg.Pipeline.MinQuestionChars, 1, 200);
        _numMinChars.Dock = DockStyle.Left;
        _numMinChars.Width = 110;

        _numMaxFail.Minimum = 1;
        _numMaxFail.Maximum = 50;
        _numMaxFail.Value = Math.Clamp(cfg.Pipeline.MaxConsecutiveFailures, 1, 50);
        _numMaxFail.Dock = DockStyle.Left;
        _numMaxFail.Width = 110;

        _txtStyle.Text = cfg.Pipeline.AnswerStyle;
        _txtStyle.Multiline = true;
        _txtStyle.ScrollBars = ScrollBars.Vertical;
        _txtStyle.Dock = DockStyle.Fill;
        _txtStyle.Height = 90;

        AddRow(lPipe, "剪贴板", _chkClipboard, 24);
        AddRow(lPipe, "最短题干", _numMinChars, 26);
        AddRow(lPipe, "失败暂停阈值", _numMaxFail, 26);
        AddRow(lPipe, "作答风格", _txtStyle, 92);

        pPipe.Controls.Add(lPipe);

        // ============ 反检测 ============
        var pStealth = new TabPage("反检测") { Padding = new Padding(14) };
        var lStealth = NewTable();

        _chkStealth.Text = "启用反检测（默认关闭）";
        _chkStealth.Checked = cfg.Stealth.Enabled;
        _chkStealth.Dock = DockStyle.Left;
        _chkStealth.Width = 300;
        _chkStealth.Font = new Font(Font, FontStyle.Bold);

        _chkStealthMemory.Text = "截图走内存（设备上不留临时文件）";
        _chkStealthMemory.Checked = cfg.Stealth.CaptureInMemory;
        _chkStealthMemory.Dock = DockStyle.Left;
        _chkStealthMemory.Width = 340;

        _chkStealthRandom.Text = "临时文件名随机化";
        _chkStealthRandom.Checked = cfg.Stealth.RandomizeTempNames;
        _chkStealthRandom.Dock = DockStyle.Left;
        _chkStealthRandom.Width = 340;

        _txtStealthDir.Text = cfg.Stealth.RemoteTempDir;
        _txtStealthDir.Dock = DockStyle.Fill;

        _numStealthJitter.Minimum = 0;
        _numStealthJitter.Maximum = 50;
        _numStealthJitter.Increment = 5;
        _numStealthJitter.Value = (decimal)Math.Clamp(cfg.Stealth.PollJitter * 100, 0, 50);
        _numStealthJitter.Dock = DockStyle.Left;
        _numStealthJitter.Width = 110;

        _chkStealthTitle.Text = "窗口标题伪装成普通工具";
        _chkStealthTitle.Checked = cfg.Stealth.DiscreetTitle;
        _chkStealthTitle.Dock = DockStyle.Left;
        _chkStealthTitle.Width = 340;

        _txtStealthTitle.Text = cfg.Stealth.WindowTitle;
        _txtStealthTitle.Dock = DockStyle.Fill;

        AddRow(lStealth, "总开关", _chkStealth, 26);
        AddRow(lStealth, "内存截图", _chkStealthMemory, 24);
        AddRow(lStealth, "文件名随机", _chkStealthRandom, 24);
        AddRow(lStealth, "临时目录", _txtStealthDir, 26);
        AddRow(lStealth, "轮询抖动(%)", _numStealthJitter, 26);
        AddRow(lStealth, "标题伪装", _chkStealthTitle, 24);
        AddRow(lStealth, "自定义标题", _txtStealthTitle, 26);

        var stealthInfo = new Label
        {
            Dock = DockStyle.Top,
            Height = 132,
            ForeColor = Color.DimGray,
            Padding = new Padding(0, 12, 0, 0),
            Text =
                "默认关闭，关掉时行为和以前完全一致。\n" +
                "\n" +
                "开启后能做：\n" +
                "  · exec-out 直接把 PNG 读到内存，设备上不落文件\n" +
                "  · 必须落盘时用隐藏名 + 随机名，且放在 /data/local/tmp\n" +
                "  · 轮询间隔加随机抖动，不再是精确节拍器\n" +
                "  · 窗口标题换成不显眼的名字\n" +
                "\n" +
                "改不了的（这些在手机侧，PC 端无能为力）：\n" +
                "  · 系统设置里的「USB 调试」开关状态\n" +
                "  · 5037 端口是否在监听\n" +
                "  · App 自己调 FLAG_SECURE 禁止截图",
        };

        pStealth.Controls.Add(stealthInfo);
        pStealth.Controls.Add(lStealth);
        tabs.TabPages.Add(pAdb);
        tabs.TabPages.Add(pCap);
        tabs.TabPages.Add(pAi);
        tabs.TabPages.Add(pPipe);
        tabs.TabPages.Add(pStealth);

        // ============ 底部 ============
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(14, 10, 14, 10),
        };
        var ok = new Button { Text = "保存", Width = 90, Height = 30, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
        var openDir = new Button { Text = "打开数据目录", Width = 116, Height = 30 };
        openDir.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", AppPaths.DataDir); } catch { }
        };

        ok.Click += (_, _) => ApplyToConfig();
        bottom.Controls.Add(ok);
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(openDir);

        Controls.Add(tabs);
        Controls.Add(bottom);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static TableLayoutPanel NewTable()
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    private static void AddRow(TableLayoutPanel t, string label, Control ctrl, int height)
    {
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, height + 10));
        t.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Height = height,
        });
        ctrl.Height = height;
        ctrl.Dock = DockStyle.Fill;
        t.Controls.Add(ctrl);
    }

    /// <summary>点「保存」后由调用方取走，写回主配置。</summary>
    public AppConfig Result => _cfg;

    private void ApplyToConfig()
    {
        _cfg.Adb.AdbPath = _txtAdbPath.Text.Trim();
        _cfg.Adb.CommandTimeoutMs = (int)_numTimeout.Value;

        _cfg.Capture.Mode = CaptureMode.Hybrid;
        // 直接用数值框的值。以前这里按档位反推 350/1500/700，
        // 用户手动填的 250 之类的值会被悄悄改掉，属于老坑。
        _cfg.Capture.PollIntervalMs = (int)_numPoll.Value;
        _cfg.Capture.SettleDelayMs = (int)_numSettle.Value;
        _cfg.Capture.KeepImages = _chkKeep.Checked;

        _cfg.Pipeline.CopyAnswerToClipboard = _chkClipboard.Checked;
        _cfg.Pipeline.MinQuestionChars = (int)_numMinChars.Value;
        _cfg.Pipeline.MaxConsecutiveFailures = (int)_numMaxFail.Value;
        _cfg.Pipeline.AnswerStyle = _txtStyle.Text.Trim();

        _cfg.Stealth.Enabled = _chkStealth.Checked;
        _cfg.Stealth.CaptureInMemory = _chkStealthMemory.Checked;
        _cfg.Stealth.RandomizeTempNames = _chkStealthRandom.Checked;
        _cfg.Stealth.RemoteTempDir = _txtStealthDir.Text.Trim();
        _cfg.Stealth.PollJitter = (double)_numStealthJitter.Value / 100.0;
        _cfg.Stealth.DiscreetTitle = _chkStealthTitle.Checked;
        _cfg.Stealth.WindowTitle = _txtStealthTitle.Text.Trim();

        _cfg.Normalize();
    }
}
