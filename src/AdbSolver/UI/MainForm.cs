using System.Diagnostics;
using AdbSolver.Adb;
using AdbSolver.Ai;
using AdbSolver.Core;
using AdbSolver.Pipeline;

namespace AdbSolver.UI;

public sealed class MainForm : Form
{
    // ---- 状态 ----
    private AppConfig _cfg;
    private AdbClient? _adb;
    private ScreenshotWatcher? _watcher;
    private AiClient? _ai;
    private SolvePipeline? _pipeline;
    private CancellationTokenSource? _pipelineCts;
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private bool _running;
    private DeviceInfo? _currentDevice;
    private int _queued;

    // ---- 控件 ----
    private readonly ComboBox _cmbDevice = new();
    private readonly Button _btnRefresh = new();
    private readonly Button _btnWifi = new();
    private readonly Button _btnToggle = new();
    private readonly Button _btnSettings = new();
    private readonly Button _btnTestShot = new();
    private readonly Label _lblDevice = new();
    private readonly Label _lblStatus = new();
    private readonly RichTextBox _txtLog = new();
    private readonly Label _lblQuestion = new();
    private readonly Label _lblAnswer = new();
    private readonly TextBox _txtSearch = new();
    private readonly ListBox _lstHistory = new();
    private readonly Label _lblStats = new();
    private readonly Panel _answerPanel = new();
    private SplitContainer? _split;
    private bool _splitInitialized;

    public MainForm()
    {
        _cfg = AppConfig.Load();

        Text = "AdbSolver · ADB 截图自动答题";
        Width = 1280;
        Height = 820;
        MinimumSize = new Size(980, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9f);
        Icon = TryLoadIcon();

        BuildUi();

        Log.OnEntry += OnLogEntry;
        FormClosing += OnFormClosing;
        Load += OnLoad;

        // 把启动前已产生的日志补上
        foreach (var e in Log.Snapshot().TakeLast(200)) AppendLog(e);
    }

    private static Icon? TryLoadIcon()
    {
        try
        {
            var p = Path.Combine(AppPaths.AppDir, "app.ico");
            if (File.Exists(p)) return new Icon(p);
        }
        catch { }
        return null;
    }

    // ================= UI 构建 =================

    private void BuildUi()
    {
        // ---------- 顶部工具条 ----------
        var top = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(10, 8, 10, 6) };

        var row1 = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        _cmbDevice.Width = 340;
        _cmbDevice.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbDevice.Margin = new Padding(0, 4, 8, 0);
        _cmbDevice.SelectedIndexChanged += (_, _) => OnDeviceSelected();

        StyleBtn(_btnRefresh, "刷新设备", 84, OnRefreshDevices);
        StyleBtn(_btnWifi, "WiFi 连接", 90, OnWifiConnect);
        StyleBtn(_btnToggle, "▶ 开始", 96, OnToggleRun);
        StyleBtn(_btnTestShot, "手动截一张", 100, OnManualShot);
        StyleBtn(_btnSettings, "设置", 72, OnSettings);

        _btnToggle.BackColor = Color.FromArgb(46, 160, 67);
        _btnToggle.ForeColor = Color.White;
        _btnToggle.FlatStyle = FlatStyle.Flat;
        _btnToggle.FlatAppearance.BorderSize = 0;

        var lblDev = new Label
        {
            Text = "设备:",
            AutoSize = false,
            Width = 42,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 6, 2, 0),
        };

        row1.Controls.AddRange(new Control[]
        {
            lblDev, _cmbDevice, _btnRefresh, _btnWifi, _btnToggle, _btnTestShot, _btnSettings,
        });

        _lblDevice.Dock = DockStyle.Top;
        _lblDevice.Height = 22;
        _lblDevice.Text = "未连接设备";
        _lblDevice.ForeColor = Color.DimGray;
        _lblDevice.Padding = new Padding(4, 0, 0, 0);

        _lblStatus.Dock = DockStyle.Top;
        _lblStatus.Height = 22;
        _lblStatus.Text = "就绪";
        _lblStatus.ForeColor = Color.FromArgb(0, 102, 204);
        _lblStatus.Padding = new Padding(4, 0, 0, 0);

        top.Controls.Add(_lblStatus);
        top.Controls.Add(_lblDevice);
        top.Controls.Add(row1);

        // ---------- 主体：左右分栏 ----------
        // 注意：SplitContainer 的 Panel1MinSize / Panel2MinSize / SplitterDistance
        // 只要在控件还没有真实宽度时设置就会抛 InvalidOperationException。
        // 所以这里一律不设，全部推迟到 OnShown → ApplySplitterLayout 里安全处理。
        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
        };
        var split = _split;

        // 左：日志
        var leftPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 4, 10) };
        var leftHead = new Label
        {
            Text = "运行日志",
            Dock = DockStyle.Top,
            Height = 22,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
        };
        _txtLog.Dock = DockStyle.Fill;
        _txtLog.ReadOnly = true;
        _txtLog.BackColor = Color.FromArgb(250, 250, 250);
        _txtLog.Font = new Font("Consolas", 9f);
        _txtLog.BorderStyle = BorderStyle.FixedSingle;
        _txtLog.WordWrap = false;
        _txtLog.ScrollBars = RichTextBoxScrollBars.Both;
        _txtLog.DetectUrls = false;

        leftPanel.Controls.Add(_txtLog);
        leftPanel.Controls.Add(leftHead);

        // 右：答案区
        var rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 4, 10, 10) };

        // 答案大字
        _answerPanel.Dock = DockStyle.Top;
        _answerPanel.Height = 190;
        _answerPanel.BackColor = Color.FromArgb(245, 250, 245);
        _answerPanel.BorderStyle = BorderStyle.FixedSingle;
        _answerPanel.Padding = new Padding(12);

        var ansHead = new Label
        {
            Text = "最终答案",
            Dock = DockStyle.Top,
            Height = 22,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(46, 120, 60),
        };
        _lblAnswer.Dock = DockStyle.Fill;
        _lblAnswer.Font = new Font("Microsoft YaHei UI", 20f, FontStyle.Bold);
        _lblAnswer.ForeColor = Color.FromArgb(20, 80, 30);
        _lblAnswer.Text = "—";
        _lblAnswer.TextAlign = ContentAlignment.MiddleLeft;
        _lblAnswer.AutoEllipsis = true;

        _answerPanel.Controls.Add(_lblAnswer);
        _answerPanel.Controls.Add(ansHead);

        // 题目
        var qPanel = new Panel { Dock = DockStyle.Top, Height = 96, Padding = new Padding(0, 8, 0, 0) };
        var qHead = new Label
        {
            Text = "识别到的题目",
            Dock = DockStyle.Top,
            Height = 20,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
        };
        _lblQuestion.Dock = DockStyle.Fill;
        _lblQuestion.BorderStyle = BorderStyle.FixedSingle;
        _lblQuestion.BackColor = Color.White;
        _lblQuestion.Padding = new Padding(6);
        _lblQuestion.Text = "—";
        _lblQuestion.AutoEllipsis = true;
        qPanel.Controls.Add(_lblQuestion);
        qPanel.Controls.Add(qHead);

        // 检索结论
        var sPanel = new Panel { Dock = DockStyle.Top, Height = 88, Padding = new Padding(0, 8, 0, 0) };
        var sHead = new Label
        {
            Text = "检索/推理结论",
            Dock = DockStyle.Top,
            Height = 20,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
        };
        _txtSearch.Dock = DockStyle.Fill;
        _txtSearch.Multiline = true;
        _txtSearch.ReadOnly = true;
        _txtSearch.ScrollBars = ScrollBars.Vertical;
        _txtSearch.BackColor = Color.White;
        _txtSearch.BorderStyle = BorderStyle.FixedSingle;
        sPanel.Controls.Add(_txtSearch);
        sPanel.Controls.Add(sHead);

        // 历史
        var hPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        var hHead = new Label
        {
            Text = "历史（双击复制答案）",
            Dock = DockStyle.Top,
            Height = 20,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
        };
        _lstHistory.Dock = DockStyle.Fill;
        _lstHistory.Font = new Font("Microsoft YaHei UI", 9.5f);
        _lstHistory.IntegralHeight = false;
        _lstHistory.DoubleClick += (_, _) => CopySelectedHistory();

        hPanel.Controls.Add(_lstHistory);
        hPanel.Controls.Add(hHead);

        rightPanel.Controls.Add(hPanel);
        rightPanel.Controls.Add(sPanel);
        rightPanel.Controls.Add(qPanel);
        rightPanel.Controls.Add(_answerPanel);

        split.Panel1.Controls.Add(leftPanel);
        split.Panel2.Controls.Add(rightPanel);

        // ---------- 底部状态栏 ----------
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 26 };
        _lblStats.Dock = DockStyle.Fill;
        _lblStats.TextAlign = ContentAlignment.MiddleLeft;
        _lblStats.Padding = new Padding(12, 0, 0, 0);
        _lblStats.ForeColor = Color.DimGray;
        _lblStats.Text = "已处理 0 题";
        bottom.Controls.Add(_lblStats);

        Controls.Add(split);
        Controls.Add(bottom);
        Controls.Add(top);
    }

    private static void StyleBtn(Button b, string text, int width, Action onClick)
    {
        b.Text = text;
        b.Width = width;
        b.Height = 30;
        b.Margin = new Padding(0, 4, 6, 0);
        b.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex) { Log.Error($"按钮处理异常: {text}", ex); }
        };
    }

    // ================= 生命周期 =================

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplySplitterLayout();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 窗口被拖到很窄时 SplitContainer 会再次抛异常，这里兜一层
        if (_splitInitialized) ApplySplitterLayout(silent: true);
    }

    /// <summary>
    /// 安全设置分割条位置。窗口宽度不够时自动降级，绝不抛异常。
    /// 顺序很重要：先把 MinSize 归零 → 设 Distance → 宽度够才恢复 MinSize。
    /// </summary>
    private void ApplySplitterLayout(bool silent = false)
    {
        if (_split is null || _split.IsDisposed) return;

        try
        {
            var total = _split.Width;
            if (total <= _split.SplitterWidth + 2) return;

            const int wantMin1 = 300;
            const int wantMin2 = 380;

            // 1) 先解除约束，避免任何中间状态触发校验
            _split.Panel1MinSize = 0;
            _split.Panel2MinSize = 0;

            var usable = total - _split.SplitterWidth;
            var target = (int)(usable * 0.52);
            target = Math.Clamp(target, 1, Math.Max(1, usable - 1));
            _split.SplitterDistance = target;

            // 2) 宽度真的够，再恢复最小尺寸
            if (usable >= wantMin1 + wantMin2)
            {
                _split.Panel1MinSize = wantMin1;
                _split.Panel2MinSize = wantMin2;
            }

            _splitInitialized = true;
        }
        catch (Exception ex)
        {
            if (!silent) Log.Warn($"设置分栏位置失败: {ex.Message}");
        }
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        Log.Info("========== AdbSolver 启动 ==========");
        Log.Info($"数据目录: {AppPaths.DataDir}");
        Log.Info($"配置文件: {AppConfig.DefaultPath}");

        _adb = new AdbClient(_cfg.Adb);
        Log.Info($"adb 路径: {_adb.AdbPath} (存在: {_adb.AdbExists})");

        if (!_adb.AdbExists)
        {
            SetStatus("未找到 adb.exe");
            MessageBox.Show(
                "没有找到 adb.exe。\n\n" +
                "请把 platform-tools 放到程序同目录，或者在「设置 → ADB」里手动指定 adb.exe 的完整路径。\n\n" +
                $"当前尝试的路径: {_adb.AdbPath}",
                "缺少 adb", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            await _adb.StartServerAsync();
        }

        RebuildAiClient();
        await RefreshDevicesAsync();

        if (_cfg.Adb.AutoConnectWifi && _cfg.Adb.WifiTargets.Count > 0)
            await AutoConnectWifiAsync();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // 关窗口必须落盘。
        // 之前这里只清理资源不保存，导致「设置」对话框用 X 关掉时（DialogResult=Cancel，
        // 主窗口那条 _cfg.Save() 根本不会执行）所有改动全丢，下次启动又是默认配置——
        // 用户看到的就是"一关软件配置全没了"。
        try { _cfg.Save(); } catch { }

        try
        {
            StopRun();
            _pipelineCts?.Cancel();
            _ai?.Dispose();
        }
        catch { }
        Log.OnEntry -= OnLogEntry;
    }

    private void RebuildAiClient()
    {
        _ai?.Dispose();
        _ai = new AiClient(_cfg.Endpoints);
        _pipeline = new SolvePipeline(_ai, _cfg.Pipeline);
        _pipeline.OnStage += stage => Ui(() => SetStatus(stage));
        Log.Info($"AI 端点已加载: {_ai.AvailableCount} 个可用");
    }

    // ================= 设备 =================

    private async void OnRefreshDevices() => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        if (_adb is null) return;
        SetStatus("正在枚举设备…");

        var devices = await _adb.ListDevicesAsync();
        Ui(() =>
        {
            var prev = _cfg.Adb.DeviceSerial;
            _cmbDevice.Items.Clear();
            foreach (var d in devices)
                _cmbDevice.Items.Add(new DeviceItem(d));

            if (_cmbDevice.Items.Count == 0)
            {
                _cmbDevice.Items.Add(new DeviceItem(new DeviceInfo
                {
                    Serial = "(无设备)",
                    State = "none",
                    Model = "",
                }));
                _cmbDevice.SelectedIndex = 0;
                SetStatus("没有检测到设备，请用 USB 连接并打开「USB 调试」");
                _lblDevice.Text = "未连接设备";
                return;
            }

            // 优先恢复上次选择
            var idx = 0;
            for (var i = 0; i < _cmbDevice.Items.Count; i++)
            {
                if (_cmbDevice.Items[i] is DeviceItem di && di.Info.Serial == prev)
                {
                    idx = i;
                    break;
                }
            }
            _cmbDevice.SelectedIndex = idx;
        });

        // 拉取详细信息（只在当前选中那台上做）
        await LoadDeviceDetailAsync();
    }

    private async Task LoadDeviceDetailAsync()
    {
        if (_adb is null) return;
        var item = _cmbDevice.SelectedItem as DeviceItem;
        if (item is null || item.Info.State == "none")
        {
            Ui(() => _lblDevice.Text = "未连接设备");
            return;
        }

        if (item.Info.State != "device")
        {
            Ui(() =>
            {
                _lblDevice.Text = $"{item.Info.Serial} — 状态: {item.Info.StateLabel}";
                _lblDevice.ForeColor = Color.Firebrick;
                SetStatus($"设备未就绪（{item.Info.StateLabel}）");
            });
            return;
        }

        SetStatus("正在读取设备信息…");
        var full = await _adb.GetDeviceInfoAsync(item.Info);
        _currentDevice = full;
        _cfg.Adb.DeviceSerial = full.Serial;
        _cfg.Save();

        Ui(() =>
        {
            _lblDevice.Text = full.Summary;
            _lblDevice.ForeColor = Color.DimGray;
            SetStatus($"设备就绪: {full.Model}");
        });

        Log.Info($"设备: {full.Summary}");
    }

    private void OnDeviceSelected()
    {
        var item = _cmbDevice.SelectedItem as DeviceItem;
        if (item is null) return;
        if (item.Info.State == "none") return;
        _cfg.Adb.DeviceSerial = item.Info.Serial;
        _ = LoadDeviceDetailAsync();

        // 运行中切了设备：监听器还盯着老设备，必须重建，否则新设备截图永远收不到
        if (_running && _watcher is not null && item.Info.Serial != _watcher.Serial)
        {
            Log.Info($"运行中切换设备 → {item.Info.Serial}，重启监听");
            _watcher.OnScreenshot -= OnNewScreenshot;
            _watcher.Stop();
            _watcher.Dispose();

            _watcher = new ScreenshotWatcher(_adb!, _cfg.Capture, item.Info.Serial);
            _watcher.OnScreenshot += OnNewScreenshot;
            _watcher.OnStatus += s => Ui(() => SetStatus(s));
            _watcher.Start();
        }
    }

    private async void OnWifiConnect()
    {
        var input = Prompt.Show(this, "WiFi / 无线调试连接",
            "输入手机的地址，格式 ip:端口\n（例：192.168.1.23:5555，或无线调试里的 192.168.1.23:37001）",
            string.Join("\n", _cfg.Adb.WifiTargets));
        if (string.IsNullOrWhiteSpace(input)) return;

        var targets = input.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        _cfg.Adb.WifiTargets = targets;
        _cfg.Save();

        if (_adb is null) return;

        SetStatus("正在连接…");
        foreach (var t in targets)
        {
            var (ok, msg) = await _adb.ConnectAsync(t);
            Log.Info($"adb connect {t} → {(ok ? "成功" : "失败")}: {msg.Replace("\n", " ")}");
        }

        await RefreshDevicesAsync();
    }

    private async Task AutoConnectWifiAsync()
    {
        if (_adb is null) return;
        foreach (var t in _cfg.Adb.WifiTargets)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            var (ok, msg) = await _adb.ConnectAsync(t);
            Log.Info($"自动连接 {t} → {(ok ? "成功" : "失败")}: {msg.Replace("\n", " ")}");
        }
        await RefreshDevicesAsync();
    }

    // ================= 运行控制 =================

    private void OnToggleRun()
    {
        if (_running) StopRun();
        else StartRun();
    }

    private void StartRun()
    {
        if (_adb is null) return;

        var dev = _currentDevice;
        if (dev is null || !dev.IsReady)
        {
            MessageBox.Show("设备还没就绪。\n请先连接手机、打开 USB 调试并允许本机调试。",
                "设备未就绪", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 监听和答题是两件事，别互相绑死。
        // 没配 AI 也要先把截图监听跑起来——至少让用户看见"图抓到了"，
        // 而不是点了开始弹个框，其实什么都没在听。
        var aiReady = _ai is not null && _ai.AvailableCount > 0;

        _pipelineCts?.Cancel();
        _pipelineCts?.Dispose();
        _pipelineCts = new CancellationTokenSource();
        _pipeline?.ResetFailureCounter();
        _queued = 0;

        _watcher = new ScreenshotWatcher(_adb, _cfg.Capture, dev.Serial);
        _watcher.OnScreenshot += OnNewScreenshot;
        _watcher.OnStatus += s => Ui(() => SetStatus(s));
        _watcher.Start();

        _running = true;
        _btnToggle.Text = "■ 停止";
        _btnToggle.BackColor = Color.FromArgb(200, 60, 60);

        if (aiReady)
        {
            SetStatus($"运行中 — 监听 {dev.Model} 的截图");
            Log.Info("=== 自动答题已启动 ===");
        }
        else
        {
            SetStatus($"只监听中 — 已开始抓图，但没配 AI，不会自动答题");
            Log.Warn("=== 已启动截图监听，但 AI 端点为空，仅抓图不答题 ===");
        }
    }

    private void StopRun()
    {
        if (!_running) return;
        _running = false;

        try
        {
            if (_watcher is not null)
            {
                _watcher.OnScreenshot -= OnNewScreenshot;
                _watcher.Stop();
                _watcher.Dispose();
            }
        }
        catch (Exception ex) { Log.Warn("停止监听异常: " + ex.Message); }
        finally { _watcher = null; }

        _btnToggle.Text = "▶ 开始";
        _btnToggle.BackColor = Color.FromArgb(46, 160, 67);
        SetStatus("已停止");
        Log.Info("=== 自动答题已停止 ===");
    }

    private void OnNewScreenshot(string path)
    {
        if (!_running) return;
        Interlocked.Increment(ref _queued);

        _ = Task.Run(async () =>
        {
            var token = _pipelineCts?.Token ?? CancellationToken.None;
            await _processGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!_running) return;
                await ProcessOneAsync(path, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error("处理截图异常", ex); }
            finally
            {
                Interlocked.Decrement(ref _queued);
                _processGate.Release();
            }
        });
    }

    private async Task ProcessOneAsync(string path, CancellationToken ct)
    {
        if (_pipeline is null) return;

        // 没配 AI 就别往下走，但要让用户明确知道"图确实抓到了"，
        // 而不是看见"跳过"就以为是监听坏了。
        if (_ai is null || _ai.AvailableCount == 0)
        {
            Ui(() =>
            {
                SetStatus($"已抓到截图（{Path.GetFileName(path)}），但没配 AI，不答题");
                _lstHistory.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  抓到截图（未配 AI）");
                while (_lstHistory.Items.Count > 300) _lstHistory.Items.RemoveAt(_lstHistory.Items.Count - 1);
            });
            return;
        }

        Ui(() => SetStatus("识别中…"));
        var outcome = await _pipeline.ProcessAsync(path, ct).ConfigureAwait(false);

        if (!outcome.Ok)
        {
            Ui(() =>
            {
                SetStatus("本次跳过：" + outcome.Error);
                UpdateStats();
            });
            return;
        }

        Ui(() =>
        {
            _lblAnswer.Text = string.IsNullOrWhiteSpace(outcome.Answer) ? "（空）" : outcome.Answer;
            _lblQuestion.Text = outcome.Question.Question.Replace("\n", "  ");
            _txtSearch.Text = string.IsNullOrWhiteSpace(outcome.SearchNotes) ? "（未启用检索）" : outcome.SearchNotes;

            var summary = $"{DateTime.Now:HH:mm:ss}  {outcome.Answer.Replace("\n", " ")}";
            _lstHistory.Items.Insert(0, summary);
            while (_lstHistory.Items.Count > 300) _lstHistory.Items.RemoveAt(_lstHistory.Items.Count - 1);

            SetStatus($"完成（{outcome.Elapsed.TotalSeconds:F1}s）");
            UpdateStats();

            if (_cfg.Pipeline.CopyAnswerToClipboard && !string.IsNullOrWhiteSpace(outcome.Answer))
            {
                try { Clipboard.SetText(outcome.Answer); } catch { }
            }
        });

        // 连续失败保护
        if (_pipeline.ConsecutiveFailures >= Math.Max(1, _cfg.Pipeline.MaxConsecutiveFailures))
        {
            Log.Warn($"连续失败 {_pipeline.ConsecutiveFailures} 次，自动暂停");
            Ui(() =>
            {
                StopRun();
                SetStatus("连续失败过多，已自动暂停。检查 AI 配置或日志。");
            });
        }
    }

    private async void OnManualShot()
    {
        if (_adb is null || _currentDevice is null || !_currentDevice.IsReady)
        {
            MessageBox.Show("设备未就绪", "提示");
            return;
        }

        SetStatus("正在抓取截图…");
        var dir = Path.Combine(AppPaths.DataDir, "captures");
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, $"manual_{DateTime.Now:HHmmss}.png");

        if (!await _adb.PullScreenshotAsync(_currentDevice.Serial, p))
        {
            SetStatus("截图失败");
            MessageBox.Show("截图失败，看看日志。", "失败");
            return;
        }

        Log.Info($"手动截图: {p}");
        await ProcessOneAsync(p, _pipelineCts?.Token ?? CancellationToken.None);
    }

    // ================= 设置 =================

    private void OnSettings()
    {
        using var dlg = new SettingsForm(_cfg, _adb);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        // 对话框里改的是一份副本，点「保存」才拷回来
        _cfg.CopyFrom(dlg.Result);

        // 先落盘再重建下游。
        // 顺序很重要：以前先建 AdbClient/AiClient 再保存，中途任何一个环节抛异常，
        // 用户刚改的配置就白改了（下次启动又回到默认）。现在保存排在最前面。
        _cfg.Save();

        _adb = new AdbClient(_cfg.Adb);
        RebuildAiClient();
        Log.Info($"配置已保存 → {AppConfig.DefaultPath}");
        SetStatus("配置已保存");
    }

    private void CopySelectedHistory()
    {
        if (_lstHistory.SelectedItem is not string s) return;
        var idx = s.IndexOf("  ", StringComparison.Ordinal);
        var answer = idx >= 0 ? s[(idx + 2)..] : s;
        try { Clipboard.SetText(answer); SetStatus("已复制到剪贴板"); } catch { }
    }

    // ================= 杂项 =================

    private void OnLogEntry(LogEntry e) => AppendLog(e);

    private void AppendLog(LogEntry e)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => AppendLog(e))); } catch { }
            return;
        }

        try
        {
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.SelectionLength = 0;
            _txtLog.SelectionColor = e.Level switch
            {
                LogLevel.Error => Color.Firebrick,
                LogLevel.Warn => Color.DarkOrange,
                LogLevel.Debug => Color.Gray,
                _ => Color.Black,
            };
            _txtLog.AppendText(e.Display + Environment.NewLine);

            if (_txtLog.TextLength > 400_000)
            {
                _txtLog.Select(0, 100_000);
                _txtLog.SelectedText = "";
            }
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.ScrollToCaret();
        }
        catch { }
    }

    private void SetStatus(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(new Action(() => SetStatus(text))); } catch { } return; }
        _lblStatus.Text = text;
    }

    private void UpdateStats()
    {
        var n = _pipeline?.ProcessedCount ?? 0;
        _lblStats.Text = $"已处理 {n} 题 · 队列 {_queued} · 端点 {_ai?.AvailableCount ?? 0} 个";
    }

    private void Ui(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(a); } catch { }
            return;
        }
        try { a(); } catch (Exception ex) { Log.Debug("UI 更新异常: " + ex.Message); }
    }

    private sealed class DeviceItem
    {
        public DeviceInfo Info { get; }
        public DeviceItem(DeviceInfo info) => Info = info;
        public override string ToString() =>
            Info.State == "none" ? "（未检测到设备）" : $"{Info.Serial}  [{Info.StateLabel}] {(string.IsNullOrEmpty(Info.Model) ? "" : "· " + Info.Model)}";
    }
}

/// <summary>简易输入框（避免依赖 Microsoft.VisualBasic）。</summary>
public static class Prompt
{
    public static string? Show(IWin32Window owner, string title, string label, string initial)
    {
        using var f = new Form
        {
            Text = title,
            Width = 480,
            Height = 240,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            Font = new Font("Microsoft YaHei UI", 9f),
        };

        var lbl = new Label { Text = label, Dock = DockStyle.Top, Height = 52, Padding = new Padding(12, 10, 12, 0) };
        var txt = new TextBox
        {
            Multiline = true,
            Dock = DockStyle.Fill,
            Text = initial,
            Margin = new Padding(12),
            ScrollBars = ScrollBars.Vertical,
        };
        var box = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 8) };
        box.Controls.Add(txt);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 8, 12, 8),
        };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 84, Height = 28 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 84, Height = 28 };
        bottom.Controls.Add(ok);
        bottom.Controls.Add(cancel);

        f.Controls.Add(box);
        f.Controls.Add(lbl);
        f.Controls.Add(bottom);
        f.AcceptButton = ok;
        f.CancelButton = cancel;

        return f.ShowDialog(owner) == DialogResult.OK ? txt.Text : null;
    }
}
