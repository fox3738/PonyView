namespace WinFormsApp1;

/// <summary>
/// 右侧信息面板：以“属性 / 值”两列列表展示当前图片的元数据（文件信息 + EXIF）。
/// 由 <see cref="MainForm"/> 通过工具栏按钮切换显示。
/// </summary>
public sealed class InfoPanel : UserControl
{
    private readonly Label _title;
    private readonly ListView _list;

    public InfoPanel()
    {
        Dock = DockStyle.Right;
        Width = 260;
        BackColor = Color.FromArgb(37, 37, 38);
        Visible = false;

        _title = new Label
        {
            Text = "图片信息",
            Dock = DockStyle.Top,
            Height = 32,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(45, 45, 48),
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold)
        };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            ShowItemToolTips = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(37, 37, 38),
            ForeColor = Color.Gainsboro,
            Font = new Font("Microsoft YaHei UI", 9f)
        };
        _list.Columns.Add("属性", 84);
        _list.Columns.Add("值", 150);

        // 先加 Fill 再加 Top，保证标题停靠在顶部、列表填满剩余区域
        Controls.Add(_list);
        Controls.Add(_title);
    }

    /// <summary>用给定的元数据填充面板。</summary>
    public void SetItems(IReadOnlyList<MetadataItem> items)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (MetadataItem item in items)
        {
            var lvi = new ListViewItem(item.Label);
            lvi.SubItems.Add(item.Value);
            lvi.ToolTipText = item.Value;
            _list.Items.Add(lvi);
        }

        _list.EndUpdate();
    }

    /// <summary>清空列表并显示一条占位提示（如“未打开图片”“正在读取…”）。</summary>
    public void ShowMessage(string message)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Items.Add(new ListViewItem(message));
        _list.EndUpdate();
    }

    /// <summary>清空面板内容。</summary>
    public void ClearInfo() => _list.Items.Clear();
}
