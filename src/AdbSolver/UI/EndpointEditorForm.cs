using System.ComponentModel;
using AdbSolver.Ai;
using AdbSolver.Core;

namespace AdbSolver.UI;

/// <summary>AI 端点编辑对话框：增删改 + 连通性测试。</summary>
public sealed class EndpointEditorForm : Form
{
    private readonly BindingList<AiEndpointConfig> _list;

    private readonly DataGridView _grid;
    private readonly TextBox _txtBaseUrl = new();
    private readonly TextBox _txtApiKey = new();

    // 模型名改成可编辑下拉框：中转站的模型名跟官方不一样，
    // 手敲十有八九被服务端以 invalid model 拒掉，所以配一个「获取模型列表」直接问服务端要。
    private readonly ComboBox _txtModel = new();

    private readonly ComboBox _cmbKind = new();
    private readonly Button _btnFetchModels = new();
    private readonly TextBox _txtName = new();
    private readonly CheckedListBox _roles = new();
    private readonly CheckBox _chkEnabled = new();

    public EndpointEditorForm(List<AiEndpointConfig> endpoints)
    {
        // 必须深拷贝再绑。BindingList 包的是原 List 的引用，
        // 直接绑上去的话「新增/删除/改字段」会立刻改到主配置那个 List 上，
        // 用户点「取消」也回不去——配置就这么被"记住"成半成品了。
        _list = new BindingList<AiEndpointConfig>(endpoints.Select(e => e.Clone()).ToList());

        Text = "AI 端点配置";
        Width = 1000;
        Height = 640;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9f);

        // ---- 左：列表 ----
        _grid = new DataGridView
        {
            Dock = DockStyle.Left,
            Width = 560,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.White,
            DataSource = _list,
        };
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        { HeaderText = "启用", DataPropertyName = nameof(AiEndpointConfig.Enabled), Width = 46 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        { HeaderText = "名称", DataPropertyName = nameof(AiEndpointConfig.Name), Width = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        { HeaderText = "协议", DataPropertyName = nameof(AiEndpointConfig.Kind), Width = 130, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        { HeaderText = "模型", DataPropertyName = nameof(AiEndpointConfig.Model), Width = 130 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        { HeaderText = "BaseUrl", DataPropertyName = nameof(AiEndpointConfig.BaseUrl), Width = 130 });
        _grid.SelectionChanged += (_, _) => LoadSelected();

        // ---- 右：编辑区 ----
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), AutoScroll = true };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
            Padding = new Padding(0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(string label, Control ctrl, int height = 30)
        {
            var lbl = new Label
            {
                Text = label,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                AutoSize = false,
                Height = height,
            };
            ctrl.Dock = DockStyle.Fill;
            ctrl.Height = height;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height + 6));
            layout.Controls.Add(lbl);
            layout.Controls.Add(ctrl);
        }

        _cmbKind.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbKind.Items.AddRange(new object[]
        {
            "OpenAI 兼容（DeepSeek/中转站等）",
            "Anthropic Claude",
            "Google Gemini",
        });

        AddRow("名称", _txtName);
        AddRow("协议", _cmbKind, 28);
        AddRow("BaseUrl", _txtBaseUrl, 28);
        AddRow("ApiKey", _txtApiKey, 28);

        // 模型行：下拉框 + 「获取模型列表」按钮，横向排一行
        _txtModel.DropDownStyle = ComboBoxStyle.DropDown;   // 可选可敲，兼容没实现 /models 的站
        _txtModel.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _txtModel.AutoCompleteSource = AutoCompleteSource.ListItems;

        var modelRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        modelRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modelRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        _txtModel.Dock = DockStyle.Fill;
        _btnFetchModels.Text = "获取模型列表";
        _btnFetchModels.Dock = DockStyle.Fill;
        _btnFetchModels.Margin = new Padding(4, 0, 0, 0);
        _btnFetchModels.Click += async (_, _) => await OnFetchModelsAsync();
        modelRow.Controls.Add(_txtModel, 0, 0);
        modelRow.Controls.Add(_btnFetchModels, 1, 0);

        AddRow("模型", modelRow, 28);

        _txtApiKey.UseSystemPasswordChar = true;

        var roleLabel = new Label
        {
            Text = "承担角色\n(全不选=全包)",
            TextAlign = ContentAlignment.TopLeft,
            Dock = DockStyle.Fill,
        };
        _roles.Items.AddRange(new object[] { "Ocr（识图）", "Search（检索）", "Solve（作答）" });
        _roles.Height = 74;
        _roles.Dock = DockStyle.Fill;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        layout.Controls.Add(roleLabel);
        layout.Controls.Add(_roles);

        _chkEnabled.Text = "启用该端点";
        _chkEnabled.Height = 26;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.Controls.Add(new Label { Text = "" });
        layout.Controls.Add(_chkEnabled);

        // 按钮区
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 8, 0, 0),
        };

        Button MkBtn(string text, Action onClick)
        {
            var b = new Button { Text = text, Width = 92, Height = 30, Margin = new Padding(0, 0, 8, 0) };
            b.Click += (_, _) => { try { onClick(); } catch (Exception ex) { MessageBox.Show(ex.Message); } };
            return b;
        }

        buttons.Controls.Add(MkBtn("新增", OnAdd));
        buttons.Controls.Add(MkBtn("删除", OnDelete));
        buttons.Controls.Add(MkBtn("保存修改", OnApply));
        buttons.Controls.Add(MkBtn("测试连接", OnTest));

        var tip = new Label
        {
            Dock = DockStyle.Top,
            Height = 60,
            Text = "提示：中转站一般选「OpenAI 兼容」，BaseUrl 填到 /v1 为止（如 https://xxx.com/v1）。\n" +
                   "填完 BaseUrl 和 ApiKey 先点「获取模型列表」——中转站的模型名跟官方不一样，手敲容易被拒。\n" +
                   "识图能力需要模型支持多模态；纯文本模型请把它只勾选 Search/Solve。",
            ForeColor = Color.DimGray,
            Padding = new Padding(0, 8, 0, 0),
        };

        right.Controls.Add(tip);
        right.Controls.Add(buttons);
        right.Controls.Add(layout);

        // 底部
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 8, 10, 8),
        };
        // 坑：「确定」原来只设了 DialogResult=OK 就直接关窗，编辑区里刚填的
        // BaseUrl / ApiKey / Model 从来没被写回列表项——用户填完直接点确定，
        // 等于白填，回到主界面还是空的。所以这里必须先 OnApplyQuiet() 再放行。
        var btnOk = new Button { Text = "确定", Width = 90, Height = 30 };
        btnOk.Click += (_, _) =>
        {
            OnApplyQuiet();
            DialogResult = DialogResult.OK;
            Close();
        };
        var btnCancel = new Button { Text = "取消", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
        bottom.Controls.Add(btnOk);
        bottom.Controls.Add(btnCancel);

        Controls.Add(right);
        Controls.Add(_grid);
        Controls.Add(bottom);

        AcceptButton = btnOk;
        CancelButton = btnCancel;

        // 坑：DataGridView 设了 DataSource 之后，行不是立刻生成的，
        // 要等控件句柄建好、绑定跑完才有 Rows。
        // 在构造函数里写 _grid.Rows[0].Selected = true 必然抛
        //   ArgumentOutOfRangeException: Index was out of range（Parameter 'index'）
        // 所以首行选中推迟到 Shown 之后，并且带数量检查。
        Shown += (_, _) => SelectFirstRowIfAny();
    }

    /// <summary>安全地选中第一行：行还没生成就延后一拍再试。</summary>
    private void SelectFirstRowIfAny()
    {
        if (_grid.Rows.Count > 0)
        {
            _grid.Rows[0].Selected = true;
            return;
        }

        // 绑定还没落地，往消息队列尾巴上挂一次重试，此时行已生成
        if (IsHandleCreated)
            BeginInvoke(new Action(() =>
            {
                if (_grid.Rows.Count > 0) _grid.Rows[0].Selected = true;
            }));
    }

    public List<AiEndpointConfig> Result => _list.ToList();

    private AiEndpointConfig? Selected =>
        _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].DataBoundItem as AiEndpointConfig : null;

    private void LoadSelected()
    {
        var ep = Selected;
        if (ep is null) return;
        _txtName.Text = ep.Name;

        // 配置里存的 Kind 可能是脏值（手改 json、旧版本枚举错位），
        // 直接 (int) 转过去再赋给 SelectedIndex 会抛 ArgumentOutOfRangeException。
        // 这里夹到合法范围。
        var kindIdx = (int)ep.Kind;
        if (kindIdx < 0 || kindIdx >= _cmbKind.Items.Count) kindIdx = 0;
        _cmbKind.SelectedIndex = kindIdx;

        _txtBaseUrl.Text = ep.BaseUrl;
        _txtApiKey.Text = ep.ApiKey;
        _txtModel.Text = ep.Model;
        _chkEnabled.Checked = ep.Enabled;
        for (var i = 0; i < _roles.Items.Count; i++)
        {
            var key = new[] { "Ocr", "Search", "Solve" }[i];
            _roles.SetItemChecked(i, ep.Roles.Any(r => string.Equals(r, key, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private void OnAdd()
    {
        var ep = new AiEndpointConfig
        {
            Name = "新端点",
            Kind = AiProviderKind.OpenAiCompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-mini",
            Enabled = true,
        };
        _list.Add(ep);

        // 同样别在行还没生成时碰 Rows，走统一的安全选中
        SelectFirstRowIfAny();
        var last = _grid.Rows.Count - 1;
        if (last >= 0) _grid.Rows[last].Selected = true;
    }

    private void OnDelete()
    {
        var ep = Selected;
        if (ep is null) return;
        if (MessageBox.Show($"确定删除端点「{ep.Name}」？", "确认", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes) return;
        _list.Remove(ep);
    }

    private void OnApply()
    {
        var ep = Selected;
        if (ep is null) return;
        ep.Name = _txtName.Text.Trim();
        ep.Kind = (AiProviderKind)Math.Max(0, _cmbKind.SelectedIndex);
        ep.BaseUrl = _txtBaseUrl.Text.Trim();
        ep.ApiKey = _txtApiKey.Text.Trim();
        ep.Model = _txtModel.Text.Trim();
        ep.Enabled = _chkEnabled.Checked;
        ep.Roles.Clear();
        var keys = new[] { "Ocr", "Search", "Solve" };
        for (var i = 0; i < _roles.Items.Count; i++)
            if (_roles.GetItemChecked(i))
                ep.Roles.Add(keys[i]);

        _grid.Refresh();
        MessageBox.Show("已保存到列表（记得点「确定」写回配置）", "OK");
    }

    private async void OnTest()
    {
        var ep = Selected;
        if (ep is null) return;

        // 先用编辑框里的值覆盖，方便边改边测
        OnApplyQuiet();

        using var ai = new AiClient(new[] { ep });
        var btnText = "测试中…";
        var owner = this;
        owner.Cursor = Cursors.WaitCursor;
        try
        {
            var r = await ai.ChatAsync("Solve", "你是一个测试助手。",
                "只回复两个字：可用", temperature: 0, maxTokens: 16);
            if (r.Ok)
                MessageBox.Show($"连接成功！\n\n端点: {r.ProviderName}\n模型: {r.Model}\n返回: {r.Text}",
                    "测试通过", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show($"连接失败：\n{r.Error}", "测试失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            owner.Cursor = Cursors.Default;
            _ = btnText;
        }
    }

    /// <summary>
    /// 向服务端要模型清单，填进下拉框。
    ///
    /// 为什么要这个：中转站的模型名跟官方不一样，同一个模型在 A 站叫
    /// `claude-sonnet-4`、在 B 站叫 `anthropic/claude-sonnet-4`、在 C 站
    /// 可能叫站主自己起的别名。手敲错了服务端只回一句 invalid model，
    /// 猜都猜不出来。直接拉清单选，省事。
    /// </summary>
    private async Task OnFetchModelsAsync()
    {
        var ep = Selected;
        if (ep is null)
        {
            MessageBox.Show("先在左边选一个端点。", "提示");
            return;
        }

        // 用编辑框里当前的 BaseUrl / ApiKey / 协议去拉，不是用已保存的旧值
        var kind = (AiProviderKind)Math.Max(0, _cmbKind.SelectedIndex);
        var baseUrl = _txtBaseUrl.Text.Trim();
        var apiKey = _txtApiKey.Text.Trim();

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            MessageBox.Show("BaseUrl 是空的，先填地址。", "提示");
            return;
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show("ApiKey 是空的，先填 Key 再拉模型列表。", "提示");
            return;
        }

        var oldText = _btnFetchModels.Text;
        _btnFetchModels.Enabled = false;
        _btnFetchModels.Text = "获取中…";
        var oldCursor = Cursor;
        Cursor = Cursors.WaitCursor;

        try
        {
            var r = await ModelCatalog.FetchAsync(kind, baseUrl, apiKey);

            if (!r.Ok)
            {
                MessageBox.Show(
                    $"没拉到模型列表：\n\n{r.Error}\n\n" +
                    "常见原因：\n" +
                    "· 这个站没实现 /models 接口（部分中转站如此）——那就只能手敲模型名\n" +
                    "· Key 没有列模型的权限\n" +
                    "· BaseUrl 填错了（OpenAI 兼容一般填到 /v1 为止）",
                    "获取失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var keep = _txtModel.Text.Trim();

            _txtModel.BeginUpdate();
            _txtModel.Items.Clear();
            foreach (var m in r.Models) _txtModel.Items.Add(m);
            _txtModel.EndUpdate();

            // 原来填的名字如果在清单里，就保留；不在就默认选第一个
            var idx = -1;
            for (var i = 0; i < _txtModel.Items.Count; i++)
            {
                if (string.Equals(_txtModel.Items[i]?.ToString(), keep, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }
            _txtModel.SelectedIndex = idx >= 0 ? idx : 0;
            _txtModel.Text = idx >= 0 ? keep : (_txtModel.Items[0]?.ToString() ?? keep);

            MessageBox.Show($"拉到 {r.Models.Count} 个模型，已填进下拉框。\n\n" +
                            "识图（Ocr 角色）要用支持多模态的模型，纯文本模型请只勾 Search/Solve。",
                "OK", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"获取模型列表出错：{ex.GetType().Name}: {ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = oldCursor;
            _btnFetchModels.Text = oldText;
            _btnFetchModels.Enabled = true;
        }
    }

    private void OnApplyQuiet()
    {
        var ep = Selected;
        if (ep is null) return;
        ep.Name = _txtName.Text.Trim();
        ep.Kind = (AiProviderKind)Math.Max(0, _cmbKind.SelectedIndex);
        ep.BaseUrl = _txtBaseUrl.Text.Trim();
        ep.ApiKey = _txtApiKey.Text.Trim();
        ep.Model = _txtModel.Text.Trim();
        ep.Enabled = _chkEnabled.Checked;
        ep.Roles.Clear();
        var keys = new[] { "Ocr", "Search", "Solve" };
        for (var i = 0; i < _roles.Items.Count; i++)
            if (_roles.GetItemChecked(i))
                ep.Roles.Add(keys[i]);
        _grid.Refresh();
    }
}
