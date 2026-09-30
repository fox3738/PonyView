using System.Drawing.Drawing2D;

namespace WinFormsApp1;

/// <summary>
/// 左侧缩略图导航栏：虚拟化显示当前文件夹下的所有图片。
/// 只解码可见区域内的缩略图（懒加载），后台线程生成并做 LRU 缓存，
/// 因此即便文件夹里有成千上万张图也不会一次性占满内存。点击缩略图切换图片。
/// </summary>
public sealed class ThumbnailSidebar : Control
{
    /// <summary>缩略图边长（像素）。</summary>
    private const int ThumbSize = 108;

    /// <summary>单元格内边距。</summary>
    private const int CellPadding = 6;

    /// <summary>文件名区域高度（两行）。</summary>
    private const int TextHeight = 30;

    /// <summary>单元格总高度。</summary>
    private const int CellHeight = ThumbSize + TextHeight + CellPadding * 2;

    /// <summary>缩略图缓存上限，超出按 LRU 淘汰。</summary>
    private const int MaxCache = 120;

    private readonly VScrollBar _scrollBar;
    private IReadOnlyList<string> _files = Array.Empty<string>();
    private string? _selectedPath;
    private int _hoverIndex = -1;

    private readonly Dictionary<string, Image> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly object _cacheSync = new();

    private readonly Queue<string> _queue = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _queueSync = new();
    private volatile bool _loaderActive;
    private volatile bool _disposedFlag;

    /// <summary>点击缩略图、请求切换到指定图片时触发。</summary>
    public event Action<string>? FileSelected;

    public ThumbnailSidebar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable,
            true);
        UpdateStyles();

        DoubleBuffered = true;
        TabStop = true;
        BackColor = Color.FromArgb(37, 37, 38);
        ForeColor = Color.Gainsboro;
        Font = new Font("Microsoft YaHei UI", 8.25f);

        // 必须先创建滚动条再设置 Dock/Width：设 Width 会触发 OnResize→UpdateScrollbar，
        // 那时若 _scrollBar 仍为 null 就会抛 NullReferenceException。
        _scrollBar = new VScrollBar
        {
            Dock = DockStyle.Right,
            Visible = false,
            SmallChange = CellHeight
        };
        _scrollBar.Scroll += (_, _) => Invalidate();
        Controls.Add(_scrollBar);

        Dock = DockStyle.Left;
        Width = 150;
        Visible = false;
    }

    /// <summary>设置要显示的文件列表（一般在切换到新文件夹时调用）。</summary>
    public void SetFiles(IReadOnlyList<string> files, string? selectedPath)
    {
        _files = files ?? Array.Empty<string>();
        _selectedPath = selectedPath;
        UpdateScrollbar();
        EnsureSelectedVisible();
        Invalidate();
    }

    /// <summary>仅更新选中项并滚动到可见位置（文件列表不变时用）。</summary>
    public void SetSelected(string? path)
    {
        if (string.Equals(_selectedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _selectedPath = path;
        EnsureSelectedVisible();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScrollbar();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);

        int contentWidth = ContentWidth;

        if (_files.Count == 0)
        {
            TextRenderer.DrawText(g, "当前没有可导航的图片", Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }

        int top = _scrollBar.Value;
        int first = Math.Max(0, top / CellHeight);
        int last = Math.Min(_files.Count - 1, (top + ClientSize.Height) / CellHeight);

        for (int i = first; i <= last; i++)
        {
            DrawCell(g, i, contentWidth);
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        int index = HitTest(e.Location);
        if (index >= 0 && index < _files.Count)
        {
            _selectedPath = _files[index];
            Invalidate();
            FileSelected?.Invoke(_files[index]);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int index = HitTest(e.Location);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!Focused && CanFocus)
        {
            Focus();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        if (!_scrollBar.Visible)
        {
            return;
        }

        int delta = e.Delta > 0 ? -CellHeight * 2 : CellHeight * 2;
        int newValue = Math.Clamp(_scrollBar.Value + delta, 0, MaxScrollValue);
        if (newValue != _scrollBar.Value)
        {
            _scrollBar.Value = newValue;
            Invalidate();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposedFlag = true;
            lock (_cacheSync)
            {
                foreach (Image img in _cache.Values)
                {
                    img.Dispose();
                }

                _cache.Clear();
                _lru.Clear();
            }
        }

        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- 布局 / 绘制

    private int ContentWidth => Math.Max(1, ClientSize.Width - (_scrollBar.Visible ? _scrollBar.Width : 0));

    private int TotalHeight => _files.Count * CellHeight;

    private int MaxScrollValue => Math.Max(0, _scrollBar.Maximum - _scrollBar.LargeChange + 1);

    private void UpdateScrollbar()
    {
        int viewport = ClientSize.Height;
        int total = TotalHeight;
        bool need = _files.Count > 0 && total > viewport;

        if (_scrollBar.Visible != need)
        {
            _scrollBar.Visible = need;
        }

        if (need)
        {
            _scrollBar.LargeChange = Math.Max(1, viewport);
            _scrollBar.Maximum = Math.Max(0, total - 1);
            if (_scrollBar.Value > MaxScrollValue)
            {
                _scrollBar.Value = MaxScrollValue;
            }
        }
    }

    private void DrawCell(Graphics g, int index, int contentWidth)
    {
        string path = _files[index];
        int y = index * CellHeight - _scrollBar.Value;
        var cell = new Rectangle(0, y, contentWidth, CellHeight);

        bool selected = string.Equals(path, _selectedPath, StringComparison.OrdinalIgnoreCase);
        bool hover = index == _hoverIndex;

        Color backColor = selected
            ? Color.FromArgb(0, 120, 215)
            : hover ? Color.FromArgb(55, 55, 58) : BackColor;
        using (var back = new SolidBrush(backColor))
        {
            g.FillRectangle(back, cell.X + 2, cell.Y + 2, cell.Width - 4, cell.Height - 4);
        }

        int thumbX = cell.X + (contentWidth - ThumbSize) / 2;
        int thumbY = cell.Y + CellPadding;
        var thumbRect = new Rectangle(thumbX, thumbY, ThumbSize, ThumbSize);

        Image? thumb = GetCached(path);
        if (thumb is not null)
        {
            DrawThumb(g, thumb, thumbRect);
        }
        else
        {
            using var placeholder = new SolidBrush(Color.FromArgb(62, 62, 66));
            g.FillRectangle(placeholder, thumbRect);
            RequestThumbnail(path); // 懒加载：仅可见单元格才会触发解码
        }

        var textRect = new Rectangle(cell.X + CellPadding, thumbY + ThumbSize + 2,
            contentWidth - CellPadding * 2, TextHeight);
        TextRenderer.DrawText(g, Path.GetFileName(path), Font, textRect,
            selected ? Color.White : ForeColor,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis |
            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
    }

    private static void DrawThumb(Graphics g, Image img, Rectangle box)
    {
        if (img.Width <= 0 || img.Height <= 0)
        {
            return;
        }

        double scale = Math.Min((double)box.Width / img.Width, (double)box.Height / img.Height);
        int w = Math.Max(1, (int)(img.Width * scale));
        int h = Math.Max(1, (int)(img.Height * scale));
        int x = box.X + (box.Width - w) / 2;
        int yy = box.Y + (box.Height - h) / 2;

        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(img, x, yy, w, h);
    }

    private int HitTest(Point p)
    {
        if (_files.Count == 0 || p.X > ContentWidth)
        {
            return -1;
        }

        int index = (p.Y + _scrollBar.Value) / CellHeight;
        return index >= 0 && index < _files.Count ? index : -1;
    }

    private void EnsureSelectedVisible()
    {
        if (_selectedPath is null || !_scrollBar.Visible)
        {
            return;
        }

        int index = -1;
        for (int i = 0; i < _files.Count; i++)
        {
            if (string.Equals(_files[i], _selectedPath, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        int itemTop = index * CellHeight;
        int itemBottom = itemTop + CellHeight;
        int viewTop = _scrollBar.Value;
        int viewBottom = viewTop + ClientSize.Height;

        if (itemTop < viewTop)
        {
            _scrollBar.Value = Math.Clamp(itemTop, 0, MaxScrollValue);
        }
        else if (itemBottom > viewBottom)
        {
            _scrollBar.Value = Math.Clamp(itemBottom - ClientSize.Height, 0, MaxScrollValue);
        }
    }

    // ---------------------------------------------------------------- 懒加载 / 缓存

    private Image? GetCached(string path)
    {
        lock (_cacheSync)
        {
            if (_cache.TryGetValue(path, out Image? img))
            {
                _lru.Remove(path);
                _lru.AddLast(path);
                return img;
            }

            return null;
        }
    }

    private void RequestThumbnail(string path)
    {
        lock (_queueSync)
        {
            if (_queued.Contains(path))
            {
                return;
            }

            _queued.Add(path);
            _queue.Enqueue(path);
        }

        EnsureLoader();
    }

    private void EnsureLoader()
    {
        if (_loaderActive || _disposedFlag)
        {
            return;
        }

        _loaderActive = true;
        Task.Run((Action)LoaderLoop);
    }

    private void LoaderLoop()
    {
        while (true)
        {
            string path;
            lock (_queueSync)
            {
                if (_queue.Count == 0)
                {
                    _loaderActive = false;
                    return;
                }

                path = _queue.Dequeue();
                _queued.Remove(path);
            }

            if (_disposedFlag)
            {
                _loaderActive = false;
                return;
            }

            Bitmap? bmp = null;
            try
            {
                // 生成 2 倍尺寸，缩放显示时更清晰
                bmp = ImageLoader.LoadThumbnail(path, ThumbSize * 2);
            }
            catch
            {
                bmp = null;
            }

            if (bmp is not null)
            {
                lock (_cacheSync)
                {
                    if (_disposedFlag)
                    {
                        bmp.Dispose();
                    }
                    else
                    {
                        AddToCacheLocked(path, bmp);
                    }
                }
            }

            if (!_disposedFlag && IsHandleCreated)
            {
                try
                {
                    BeginInvoke(new Action(Invalidate));
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    // 控件正在销毁或已释放，忽略
                }
            }
        }
    }

    /// <summary>写入缓存并按 LRU 淘汰，调用前须持有 <see cref="_cacheSync"/>。</summary>
    private void AddToCacheLocked(string path, Image img)
    {
        if (_cache.TryGetValue(path, out Image? old))
        {
            old.Dispose();
            _cache.Remove(path);
            _lru.Remove(path);
        }

        _cache[path] = img;
        _lru.AddLast(path);

        while (_lru.Count > MaxCache)
        {
            LinkedListNode<string>? first = _lru.First;
            if (first is null)
            {
                break;
            }

            _lru.RemoveFirst();
            if (_cache.TryGetValue(first.Value, out Image? evict))
            {
                evict.Dispose();
                _cache.Remove(first.Value);
            }
        }
    }
}
