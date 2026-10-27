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
    private readonly TextBox _txtModel = new();
    private readonly ComboBox _cmbKind = new();
    private readonly TextBox _txtName = new();
    private readonly CheckedListBox _roles = new();
    private readonly CheckBox _chkEnabled = new();

    public EndpointEditorForm(List<AiEndpointConfig> endpoints)
    {
        _list = new BindingList<AiEndpointConfig>(endpoints);

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
        AddRow("模型", _txtModel, 28);

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
        var btnOk = new Button { Text = "确定", Width = 90, Height = 30, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
        bottom.Controls.Add(btnOk);
        bottom.Controls.Add(btnCancel);

        Controls.Add(right);
        Controls.Add(_grid);
        Controls.Add(bottom);

        AcceptButton = btnOk;
        CancelButton = btnCancel;

        if (_list.Count > 0) _grid.Rows[0].Selected = true;
    }

    public List<AiEndpointConfig> Result => _list.ToList();

    private AiEndpointConfig? Selected =>
        _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].DataBoundItem as AiEndpointConfig : null;

    private void LoadSelected()
    {
        var ep = Selected;
        if (ep is null) return;
        _txtName.Text = ep.Name;
        _cmbKind.SelectedIndex = (int)ep.Kind;
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
        _grid.Rows[_grid.Rows.Count - 1].Selected = true;
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
