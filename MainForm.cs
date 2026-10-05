namespace WinFormsApp1;

/// <summary>
/// 图片查看器主窗口：工具栏 + 图片显示区 + 状态栏。
/// 解码在后台线程进行，避免大图（RAW / PSD / HEIC）卡住界面。
/// </summary>
public sealed class MainForm : Form
{
    private readonly ZoomPanPictureBox _pictureBox;
    private readonly Toolbar _toolbar;
    private readonly Statusbar _statusbar;
    private readonly InfoPanel _infoPanel;
    private readonly ThumbnailSidebar _thumbnails;
    private readonly MenuStrip _menuStrip;
    private readonly ToolStripMenuItem _fileMenu;
    private readonly ContextMenuStrip _contextMenu;

    private IReadOnlyList<string> _files = Array.Empty<string>();
    private int _currentIndex = -1;
    private string? _currentPath;
    private DecodedImage? _current;

    // 解码不可中途取消，改用序号丢弃过期结果，避免切换过快时旧解码结果泄漏
    private int _loadSequence;
    private bool _busy;

    private bool _fullScreen;
    private FormBorderStyle _restoreBorderStyle = FormBorderStyle.Sizable;
    private Rectangle _restoreBounds;

    // 全屏前的面板可见状态与图片区背景色，退出全屏时还原
    private bool _thumbsVisibleBeforeFullScreen;
    private bool _infoVisibleBeforeFullScreen;
    private Color _pictureBackColor = Color.FromArgb(45, 45, 48);

    // 缩略图侧栏当前对应的文件夹，未变时不重新设列表以免滚动位置被重置
    private string? _thumbDir;

    /// <summary>启动时（命令行 / 打开方式）传入的图片路径。</summary>
    public string? InitialImagePath { get; set; }

    /// <summary>应用显示名（中文）。改名字只需改这一处，窗口标题的三处引用会跟着变。</summary>
    private const string AppTitle = "小马看图";

    public MainForm()
    {
        Text = AppTitle;
        // 窗口图标直接从 exe 提取（exe 已通过 ApplicationIcon 嵌入多尺寸 app.ico），无需额外文件
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1100, 720);
        MinimumSize = new Size(480, 320);
        AllowDrop = true;
        KeyPreview = true;

        _pictureBox = new ZoomPanPictureBox();
        _toolbar = new Toolbar();
        _statusbar = new Statusbar();
        _infoPanel = new InfoPanel();
        _thumbnails = new ThumbnailSidebar();

        (_menuStrip, _fileMenu, _contextMenu) = BuildMenus();

        WireEvents();

        // 停靠顺序（后加的靠外/靠顶）：先 Fill 图片区，再左/右侧栏，最后顶/底边条与 MenuStrip
        Controls.Add(_pictureBox);
        Controls.Add(_thumbnails);
        Controls.Add(_infoPanel);
        Controls.Add(_statusbar.Strip);
        Controls.Add(_toolbar.Strip);
        Controls.Add(_menuStrip);
        MainMenuStrip = _menuStrip;

        UpdateUiState();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // 后台预热“格式转换为”菜单所需的 Magick 可写格式列表，避免在 UI 线程上
        // 首次加载 Magick.NET 原生库造成窗口已显示后的卡顿。不影响启动速度。
        _ = Task.Run(() => { _ = ImageLoader.WritableFormats; });

        if (!string.IsNullOrWhiteSpace(InitialImagePath) && File.Exists(InitialImagePath))
        {
            _ = LoadImageAsync(InitialImagePath!);
        }
    }

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        base.OnDragEnter(drgevent);
        drgevent.Effect = ImageLoader.ExtractImagePath(drgevent.Data) is not null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);

        string? path = ImageLoader.ExtractImagePath(drgevent.Data);
        if (path is not null)
        {
            _ = LoadImageAsync(path);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _loadSequence++; // 让在飞的解码结果作废
        base.OnFormClosed(e);
    }

    /// <summary>快捷键统一在此处理，优先级高于子控件。</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.O:
                OpenFile();
                return true;

            case Keys.Control | Keys.C:
                CopyImageToClipboard();
                return true;

            case Keys.Control | Keys.S:
                Save();
                return true;

            case Keys.Control | Keys.Shift | Keys.S:
                SaveAs();
                return true;

            case Keys.Control | Keys.W:
                CloseImage();
                return true;

            case Keys.Left:
                ShowPrevious();
                return true;

            case Keys.Right:
                ShowNext();
                return true;

            case Keys.Control | Keys.Left:
                RotateBy(-90);
                return true;

            case Keys.Control | Keys.Right:
                RotateBy(90);
                return true;

            case Keys.Space:
                TogglePlayback();
                return true;

            case Keys.Oemcomma:
                StepFrame(-1);
                return true;

            case Keys.OemPeriod:
                StepFrame(1);
                return true;

            case Keys.Add:
            case Keys.Oemplus:
            case Keys.Oemplus | Keys.Shift:
                ZoomBy(1.25f);
                return true;

            case Keys.Subtract:
            case Keys.OemMinus:
                ZoomBy(1f / 1.25f);
                return true;

            case Keys.D0:
            case Keys.NumPad0:
                _pictureBox.FitToWindow();
                FocusPicture();
                return true;

            case Keys.D1:
            case Keys.NumPad1:
                _pictureBox.ActualSize();
                FocusPicture();
                return true;

            case Keys.F11:
                ToggleFullScreen();
                return true;

            case Keys.Escape when _fullScreen:
                ToggleFullScreen();
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DecodedImage? image = _current;
            _current = null;

            if (image is not null)
            {
                image.RasterUpdated -= OnRasterUpdated;
            }

            // 图片实例归内存缓存所有，统一在此清空并释放
            ImageLoader.ClearCache();
        }

        base.Dispose(disposing);
    }

    private void WireEvents()
    {
        _pictureBox.ZoomChanged += zoom => _statusbar.ZoomLabel.Text = $"{zoom * 100:F0}%";
        _pictureBox.PlaybackChanged += UpdatePlaybackUi;
        _pictureBox.FrameChanged += index => UpdateFrameUi(index);

        _toolbar.PrevButton.Click += (_, _) => ShowPrevious();
        _toolbar.NextButton.Click += (_, _) => ShowNext();
        _toolbar.ZoomInButton.Click += (_, _) => ZoomBy(1.25f);
        _toolbar.ZoomOutButton.Click += (_, _) => ZoomBy(1f / 1.25f);
        _toolbar.FitButton.Click += (_, _) =>
        {
            _pictureBox.FitToWindow();
            FocusPicture();
        };
        _toolbar.ActualButton.Click += (_, _) =>
        {
            _pictureBox.ActualSize();
            FocusPicture();
        };
        _toolbar.RotateLeftButton.Click += (_, _) => RotateBy(-90);
        _toolbar.RotateRightButton.Click += (_, _) => RotateBy(90);
        _toolbar.PlayButton.Click += (_, _) => TogglePlayback();
        _toolbar.ExportFramesButton.Click += (_, _) => _ = ExportAnimatedFramesAsync();
        _toolbar.NavButton.Click += (_, _) => ToggleThumbnails();
        _toolbar.InfoButton.Click += (_, _) => ToggleInfoPanel();
        _thumbnails.FileSelected += path => _ = LoadImageAsync(path);
        _pictureBox.RequestToggleFullScreen += ToggleFullScreen;

        _toolbar.RedButton.Click += (_, _) => SetChannelMode(ChannelMode.Red);
        _toolbar.GreenButton.Click += (_, _) => SetChannelMode(ChannelMode.Green);
        _toolbar.BlueButton.Click += (_, _) => SetChannelMode(ChannelMode.Blue);
        _toolbar.AlphaButton.Click += (_, _) => SetChannelMode(ChannelMode.Alpha);
        _toolbar.FullButton.Click += (_, _) => SetChannelMode(ChannelMode.Full);
        _pictureBox.ChannelModeChanged += _toolbar.UpdateChannelButtons;
    }

    private void OpenFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "打开图片",
            Filter = ImageLoader.DialogFilter,
            CheckFileExists = true,
            Multiselect = false
        };

        if (_currentPath is not null)
        {
            string? dir = Path.GetDirectoryName(_currentPath);
            if (!string.IsNullOrEmpty(dir))
            {
                dialog.InitialDirectory = dir;
                dialog.FileName = Path.GetFileName(_currentPath);
            }
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _ = LoadImageAsync(dialog.FileName);
        }

        FocusPicture();
    }

    // ---------------------------------------------------------------- 保存 / 转存 / 关闭

    /// <summary>保存：静态图弹对话框（默认指向原文件、原格式）确认后覆盖；动态图或源格式不可写时回退为“另存为”。</summary>
    private void Save()
    {
        if (_current is null || _busy)
        {
            return;
        }

        string ext = Path.GetExtension(_current.FilePath);
        WriteFormat format = ImageLoader.FormatForExtension(ext);

        // 动态图，或源格式不可写（HEIC/SVG/RAW 等，FormatForExtension 会回退成 PNG）时，走“另存为”
        if (_current.IsAnimated || !string.Equals(format.Extension, ext, StringComparison.OrdinalIgnoreCase))
        {
            SaveAs();
            return;
        }

        _ = SaveAsCore(_current.FilePath, format);
    }

    /// <summary>另存为：弹对话框选路径与格式；动态图只保存第一帧为单张静态图（序列帧请用工具栏“导出序列帧”）。</summary>
    private void SaveAs()
    {
        if (_current is null || _busy)
        {
            return;
        }

        string suggested = Path.Combine(
            Path.GetDirectoryName(_current.FilePath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(_current.FilePath) + ".png");
        _ = SaveAsCore(suggested, new WriteFormat(ImageMagick.MagickFormat.Png, ".png", "PNG 图片"));
    }

    /// <summary>静态图另存为：弹“另存为”对话框，确认后后台转存为单个目标文件。</summary>
    private async Task SaveAsCore(string suggestedPath, WriteFormat suggestedFormat)
    {
        DecodedImage? source = _current;
        if (source is null)
        {
            return;
        }

        int rotation = _pictureBox.Rotation;

        using var dialog = new SaveFileDialog
        {
            Title = "另存为",
            Filter = ImageLoader.SaveDialogFilter,
            FileName = Path.GetFileName(suggestedPath),
            AddExtension = true,
            OverwritePrompt = true,
            DefaultExt = "png"
        };

        string? dir = Path.GetDirectoryName(suggestedPath);
        if (!string.IsNullOrEmpty(dir))
        {
            dialog.InitialDirectory = dir;
        }

        dialog.FilterIndex = FilterIndexOf(suggestedFormat.Extension);

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            FocusPicture();
            return;
        }

        WriteFormat format = ImageLoader.FormatForExtension(Path.GetExtension(dialog.FileName));
        string target = Path.ChangeExtension(dialog.FileName, format.Extension);
        await RunExportAsync($"正在转存 {Path.GetFileName(target)} …",
            () => ImageLoader.SaveStatic(source, target, format.Format, rotation),
            () => $"已转存为 {format.Label}：\n{target}");
    }

    /// <summary>格式转换为：直接按指定格式写到源文件同目录、同名不同扩展名的文件（动态图只转换第一帧）。</summary>
    private void ConvertToFormat(WriteFormat format)
    {
        if (_current is null || _busy)
        {
            return;
        }

        string dir = Path.GetDirectoryName(_current.FilePath) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(_current.FilePath);
        string target = Path.Combine(dir, baseName + format.Extension);

        DecodedImage source = _current;
        int rotation = _pictureBox.Rotation;
        _ = RunExportAsync($"正在转换为 {format.Label} …",
            () => ImageLoader.SaveStatic(source, target, format.Format, rotation),
            () => $"已转换为 {format.Label}：\n{target}");
    }

    /// <summary>
    /// 复制当前显示的图片到系统剪贴板，供其他软件粘贴。
    /// 取当前帧（动态图为正在显示的那一帧）并套用当前旋转，做到“所见即所得”。
    /// </summary>
    private void CopyImageToClipboard()
    {
        DecodedImage? source = _current;
        if (source is null || _busy)
        {
            return;
        }

        try
        {
            // GetFrame 返回的位图归缓存所有，只克隆出独立副本操作，绝不 Dispose 原对象（所有权契约）
            using Bitmap copy = CloneFrame(source.GetFrame(_pictureBox.CurrentFrameIndex), _pictureBox.Rotation);
            Clipboard.SetImage(copy);
            _statusbar.InfoLabel.Text = "已复制图片到剪贴板";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"复制到剪贴板失败：{DescribeSaveError(ex)}", "复制失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        FocusPicture();
    }

    /// <summary>
    /// 把缓存拥有的帧复制成一张独立的 32bpp 位图并套用旋转（90 的倍数）。返回的位图归调用方所有，用完需 Dispose。
    /// </summary>
    private static Bitmap CloneFrame(Image frame, int rotationDegrees)
    {
        var copy = new Bitmap(frame.Width, frame.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(copy))
        {
            g.DrawImage(frame, 0, 0, frame.Width, frame.Height);
        }

        int normalized = ((rotationDegrees % 360) + 360) % 360;
        switch (normalized)
        {
            case 90:
                copy.RotateFlip(RotateFlipType.Rotate90FlipNone);
                break;
            case 180:
                copy.RotateFlip(RotateFlipType.Rotate180FlipNone);
                break;
            case 270:
                copy.RotateFlip(RotateFlipType.Rotate270FlipNone);
                break;
        }

        return copy;
    }

    /// <summary>动态图导出：选父文件夹，在其下建 <c>{源名}_frames</c> 并写入 PNG 序列帧。</summary>
    private async Task ExportAnimatedFramesAsync()
    {
        DecodedImage? source = _current;
        if (source is null)
        {
            return;
        }

        int rotation = _pictureBox.Rotation;
        string baseName = Path.GetFileNameWithoutExtension(source.FilePath);

        using var dialog = new FolderBrowserDialog
        {
            Description = "选择用于存放序列帧的父文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        string? dir = Path.GetDirectoryName(source.FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            dialog.SelectedPath = dir;
        }

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            FocusPicture();
            return;
        }

        string targetFolder = Path.Combine(dialog.SelectedPath, baseName + "_frames");
        int count = 0;
        await RunExportAsync("正在导出序列帧 …",
            () => count = ImageLoader.SaveAnimatedFrames(source, targetFolder, rotation, baseName),
            () => $"已导出 {count} 帧 PNG 序列到：\n{targetFolder}");
    }

    /// <summary>关闭当前图片，回到未打开状态。</summary>
    private void CloseImage()
    {
        if (_current is null)
        {
            return;
        }

        _loadSequence++; // 让在飞的解码/重栅格化结果作废

        DecodedImage? image = _current;
        string? path = _currentPath;
        _current = null;
        _currentPath = null;

        if (image is not null)
        {
            image.RasterUpdated -= OnRasterUpdated;
        }

        try
        {
            _pictureBox.SetImage(null);
            _pictureBox.ChannelMode = ChannelMode.Full;
            _thumbnails.SetSelected(null);
            _infoPanel.ClearInfo();

            Text = AppTitle;
            _statusbar.SetPath("未打开图片");
            _statusbar.InfoLabel.Text = string.Empty;
            _statusbar.InfoLabel.ToolTipText = string.Empty;
            _statusbar.WarningLabel.Visible = false;
            _statusbar.FrameLabel.Text = string.Empty;
            _statusbar.ZoomLabel.Text = string.Empty;

            UpdateUiState();
            UpdatePlaybackUi();
        }
        finally
        {
            // 控件已停止绘制；放进 finally 确保即使上面的 UI 清理抛异常也不会漏掉 Unpin，防止固定泄漏。
            // 实例归缓存所有，超出预算时由缓存淘汰释放。
            if (path is not null)
            {
                ImageLoader.Unpin(path);
            }
        }
    }

    /// <summary>统一的导出执行：后台线程跑写入动作，期间置忙，完成后弹结果。</summary>
    private async Task RunExportAsync(string busyMessage, Action action, Func<string> success)
    {
        SetBusy(true, busyMessage);
        string? error = null;
        try
        {
            await Task.Run(action).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            error = DescribeSaveError(ex);
        }

        SetBusy(false, null);
        ShowSaveResult(error, error is null ? success() : string.Empty);
        FocusPicture();
    }

    private static string DescribeSaveError(Exception ex) =>
        ex is ImageDecodeException ide ? ide.Message : $"{ex.GetType().Name}: {ex.Message}";

    private void ShowSaveResult(string? error, string successMessage)
    {
        if (error is null)
        {
            MessageBox.Show(this, successMessage, "转存完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(this, error, "转存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>“另存为”过滤器里某扩展名对应的 1 基序号，找不到返回 1。</summary>
    private static int FilterIndexOf(string extension)
    {
        IReadOnlyList<WriteFormat> formats = ImageLoader.WritableFormats;
        for (int i = 0; i < formats.Count; i++)
        {
            if (string.Equals(formats[i].Extension, extension, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 1;
    }

    // ---------------------------------------------------------------- 菜单 / 通道

    /// <summary>构建顶部“文件”菜单与图片区右键菜单（两者共用同一套动作工厂）。</summary>
    private (MenuStrip Menu, ToolStripMenuItem FileMenu, ContextMenuStrip Context) BuildMenus()
    {
        var fileMenu = new ToolStripMenuItem("文件(&F)");
        fileMenu.DropDownItems.AddRange(CreateFileActions());
        fileMenu.DropDownOpening += (_, _) => RefreshActionItems(fileMenu.DropDownItems);

        var menuStrip = new MenuStrip();
        menuStrip.Items.Add(fileMenu);

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.AddRange(CreateFileActions());
        contextMenu.Opening += OnContextMenuOpening;
        _pictureBox.ContextMenuStrip = contextMenu;

        return (menuStrip, fileMenu, contextMenu);
    }

    /// <summary>创建“打开 / 保存 / 另存为 / 格式转换为 / 复制图片 / 关闭”菜单项（每次调用返回全新实例，供菜单与右键菜单分别使用）。</summary>
    private ToolStripItem[] CreateFileActions()
    {
        var open = new ToolStripMenuItem("打开(&O)…") { ShortcutKeyDisplayString = "Ctrl+O" };
        open.Click += (_, _) => OpenFile();

        var save = new ToolStripMenuItem("保存(&S)") { ShortcutKeyDisplayString = "Ctrl+S" };
        save.Click += (_, _) => Save();

        var saveAs = new ToolStripMenuItem("另存为(&A)…") { ShortcutKeyDisplayString = "Ctrl+Shift+S" };
        saveAs.Click += (_, _) => SaveAs();

        // 不在构造时枚举可写格式：那会提前加载 Magick.NET 原生库（约 30MB）并枚举
        // 全部可写格式，拖慢启动。改为懒填充——首次展开子菜单时再填；OnShown 里已
        // 在后台预热，正常情况下用户展开时列表已就绪，无卡顿。
        var convert = new ToolStripMenuItem("格式转换为(&F)");
        bool convertFilled = false;
        convert.DropDownOpening += (_, _) =>
        {
            if (convertFilled)
            {
                return;
            }

            convertFilled = true;
            foreach (WriteFormat format in ImageLoader.WritableFormats)
            {
                WriteFormat captured = format;
                var item = new ToolStripMenuItem($"{format.Label} (*{format.Extension})");
                item.Click += (_, _) => ConvertToFormat(captured);
                convert.DropDownItems.Add(item);
            }
        };

        var copy = new ToolStripMenuItem("复制图片(&P)") { ShortcutKeyDisplayString = "Ctrl+C" };
        copy.Click += (_, _) => CopyImageToClipboard();

        var close = new ToolStripMenuItem("关闭(&C)") { ShortcutKeyDisplayString = "Ctrl+W" };
        close.Click += (_, _) => CloseImage();

        return new ToolStripItem[] { open, new ToolStripSeparator(), save, saveAs, convert, copy, new ToolStripSeparator(), close };
    }

    /// <summary>根据当前是否有图 / 是否动态图，刷新一组菜单项的可用状态。</summary>
    private void RefreshActionItems(System.Windows.Forms.ToolStripItemCollection items)
    {
        bool hasImage = _current is not null && !_busy;
        bool animated = _current?.IsAnimated == true;

        foreach (ToolStripItem item in items)
        {
            switch (item)
            {
                case ToolStripMenuItem mi when mi.Text?.StartsWith("打开") == true:
                    mi.Enabled = !_busy;
                    break;
                case ToolStripMenuItem mi when mi.Text?.StartsWith("保存") == true:
                    mi.Enabled = hasImage && !animated;
                    break;
                case ToolStripMenuItem mi when mi.Text?.StartsWith("另存为") == true:
                    mi.Enabled = hasImage;
                    break;
                case ToolStripMenuItem mi when mi.Text?.StartsWith("格式转换") == true:
                    mi.Enabled = hasImage && !animated && mi.DropDownItems.Count > 0;
                    break;
                case ToolStripMenuItem mi when mi.Text?.StartsWith("复制") == true:
                    mi.Enabled = hasImage; // 动态图也允许，复制当前显示的那一帧
                    break;
                case ToolStripMenuItem mi when mi.Text?.StartsWith("关闭") == true:
                    mi.Enabled = hasImage;
                    break;
            }
        }
    }

    /// <summary>设置通道显示模式并同步工具栏按钮选中态。</summary>
    private void SetChannelMode(ChannelMode mode)
    {
        _pictureBox.ChannelMode = mode;
        _toolbar.UpdateChannelButtons(mode);
    }

    // ---------------------------------------------------------------- 信息面板 / 缩略图导航

    /// <summary>切换右侧信息面板的显示，打开时异步读取当前图片的元数据。</summary>
    private void ToggleInfoPanel()
    {
        bool show = !_infoPanel.Visible;
        _infoPanel.Visible = show;
        _toolbar.InfoButton.Checked = show;

        if (show)
        {
            _ = RefreshInfoPanelAsync();
        }

        FocusPicture();
    }

    /// <summary>切换左侧缩略图导航栏的显示，打开时同步当前文件夹与选中项。</summary>
    private void ToggleThumbnails()
    {
        bool show = !_thumbnails.Visible;
        _thumbnails.Visible = show;
        _toolbar.NavButton.Checked = show;

        if (show)
        {
            SyncThumbnails();
        }

        FocusPicture();
    }

    /// <summary>后台读取当前图片元数据并填充信息面板；期间切换图片或关闭面板则丢弃过期结果。</summary>
    private async Task RefreshInfoPanelAsync()
    {
        DecodedImage? source = _current;
        if (source is null)
        {
            _infoPanel.ShowMessage("未打开图片");
            return;
        }

        _infoPanel.ShowMessage("正在读取信息…");
        int sequence = _loadSequence;

        IReadOnlyList<MetadataItem> items;
        try
        {
            items = await Task.Run(() => ImageLoader.ReadMetadata(source)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (sequence == _loadSequence && _infoPanel.Visible)
            {
                _infoPanel.ShowMessage($"读取信息失败：{ex.Message}");
            }

            return;
        }

        if (sequence != _loadSequence || !_infoPanel.Visible)
        {
            return;
        }

        _infoPanel.SetItems(items);
    }

    /// <summary>把缩略图侧栏与当前文件夹 / 选中项同步；文件夹未变时只更新选中项，避免重置滚动。</summary>
    private void SyncThumbnails()
    {
        string? dir = _currentPath is null ? null : Path.GetDirectoryName(_currentPath);
        if (!string.Equals(dir, _thumbDir, StringComparison.OrdinalIgnoreCase))
        {
            _thumbDir = dir;
            _thumbnails.SetFiles(_files, _currentPath);
        }
        else
        {
            _thumbnails.SetSelected(_currentPath);
        }
    }

    /// <summary>右键菜单弹出前：全屏时取消菜单并退出全屏，否则刷新菜单项可用状态。</summary>
    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_fullScreen)
        {
            e.Cancel = true;
            ToggleFullScreen();
            return;
        }

        if (sender is ContextMenuStrip menu)
        {
            RefreshActionItems(menu.Items);
        }
    }

    /// <summary>
    /// 在后台线程解码并显示指定路径的图片。快速连续切换时，过期的解码结果会被丢弃并释放。
    /// </summary>
    private async Task LoadImageAsync(string path)
    {
        int sequence = ++_loadSequence;

        SetBusy(true, $"正在解码 {Path.GetFileName(path)} …");

        DecodedImage? decoded = null;
        string? error = null;

        try
        {
            // 异步加载：命中内存缓存直接复用，否则后台解码；pin:true 固定当前图，防止被后台预加载淘汰
            decoded = await ImageLoader.LoadAsync(path, pin: true).ConfigureAwait(true);
        }
        catch (ImageDecodeException ex)
        {
            error = ex.Message;
        }
        catch (OutOfMemoryException)
        {
            error = "图片尺寸过大或数据已损坏，内存不足。";
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        // 期间又发起了新的加载，本次结果作废：解除本次固定即可，实例归缓存所有，不能 Dispose
        if (sequence != _loadSequence)
        {
            if (decoded is not null)
            {
                ImageLoader.Unpin(path);
            }

            return;
        }

        if (decoded is null)
        {
            SetBusy(false, null);
            MessageBox.Show(this, $"无法打开图片：{Path.GetFileName(path)}\n\n{error}", "打开失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        DecodedImage? previous = _current;
        string? previousPath = _currentPath;
        bool committed = false;
        try
        {
            _current = decoded;
            _currentPath = path;
            decoded.RasterUpdated += OnRasterUpdated;
            _pictureBox.SetImage(decoded);
            _pictureBox.ChannelMode = ChannelMode.Full; // 新图片回到完整显示
            _toolbar.UpdateChannelButtons(ChannelMode.Full);

            // 刷新同目录下的图片列表，便于上一张 / 下一张浏览
            _files = ImageLoader.GetImageFiles(path);
            _currentIndex = IndexOf(_files, path);

            Text = $"{AppTitle} - {Path.GetFileName(path)}";
            _statusbar.SetPath(path);
            UpdateInfoLabel(decoded);
            _statusbar.WarningLabel.Visible = decoded.Warnings.Count > 0;
            _statusbar.WarningLabel.ToolTipText = string.Join(Environment.NewLine, decoded.Warnings);
            _statusbar.ZoomLabel.Text = $"{_pictureBox.Zoom * 100:F0}%";

            SetBusy(false, null);
            UpdateUiState();
            UpdatePlaybackUi();
            PreloadNeighbors(); // 提前在后台解码相邻图片，切换时命中缓存

            if (_thumbnails.Visible)
            {
                SyncThumbnails();
            }

            if (_infoPanel.Visible)
            {
                _ = RefreshInfoPanelAsync();
            }

            FocusPicture();
            committed = true;
        }
        finally
        {
            // 无论 UI 流程是否抛异常，都保证固定计数配对释放，避免 pin 泄漏导致内存永远无法回收。
            if (committed)
            {
                // 新图已成功接管显示：解除上一张的固定（实例归缓存所有，超出预算时由缓存淘汰释放）
                if (previous is not null)
                {
                    previous.RasterUpdated -= OnRasterUpdated;
                    if (previousPath is not null)
                    {
                        ImageLoader.Unpin(previousPath);
                    }
                }
            }
            else
            {
                // UI 流程异常：回退到上一张，并释放本次为新图加的固定
                _current = previous;
                _currentPath = previousPath;
                decoded.RasterUpdated -= OnRasterUpdated;
                ImageLoader.Unpin(path);
            }
        }
    }

    /// <summary>在后台预加载相邻（下一张 / 上一张）图片进缓存，使来回浏览时能直接命中，无需重新解码。</summary>
    private void PreloadNeighbors()
    {
        if (_files.Count <= 1 || _currentIndex < 0)
        {
            return;
        }

        string next = _files[(_currentIndex + 1) % _files.Count];
        _ = ImageLoader.PreloadAsync(next);

        string prev = _files[(_currentIndex - 1 + _files.Count) % _files.Count];
        if (!string.Equals(prev, next, StringComparison.OrdinalIgnoreCase))
        {
            _ = ImageLoader.PreloadAsync(prev);
        }
    }

    private void UpdateInfoLabel(DecodedImage decoded)
    {
        _statusbar.InfoLabel.Text = ImageLoader.Describe(decoded);
        _statusbar.InfoLabel.ToolTipText = decoded.Warnings.Count > 0
            ? string.Join(Environment.NewLine, decoded.Warnings)
            : decoded.FilePath;
    }

    /// <summary>SVG 按当前缩放重新栅格化完成后刷新状态栏里的分辨率信息。</summary>
    private void OnRasterUpdated()
    {
        DecodedImage? decoded = _current;
        if (decoded is null)
        {
            return;
        }

        UpdateInfoLabel(decoded);
    }

    private void SetBusy(bool busy, string? message)
    {
        _busy = busy;
        UseWaitCursor = busy;
        _statusbar.FrameLabel.Text = message ?? string.Empty;
        UpdateUiState();
    }

    private void ShowPrevious()
    {
        if (_files.Count == 0 || _currentIndex < 0 || _busy)
        {
            return;
        }

        _ = LoadImageAsync(_files[(_currentIndex - 1 + _files.Count) % _files.Count]);
    }

    private void ShowNext()
    {
        if (_files.Count == 0 || _currentIndex < 0 || _busy)
        {
            return;
        }

        _ = LoadImageAsync(_files[(_currentIndex + 1) % _files.Count]);
    }

    private void ZoomBy(float factor)
    {
        if (!_pictureBox.HasImage)
        {
            return;
        }

        _pictureBox.ZoomBy(factor);
        FocusPicture();
    }

    private void RotateBy(int degrees)
    {
        if (!_pictureBox.HasImage)
        {
            return;
        }

        _pictureBox.Rotate(degrees);
        FocusPicture();
    }

    private void TogglePlayback()
    {
        if (!_pictureBox.IsAnimated)
        {
            return;
        }

        _pictureBox.TogglePlayback();
        FocusPicture();
    }

    private void StepFrame(int delta)
    {
        if (!_pictureBox.IsAnimated)
        {
            return;
        }

        _pictureBox.StepFrame(delta);
        UpdateFrameUi(_pictureBox.CurrentFrameIndex);
        FocusPicture();
    }

    private void UpdatePlaybackUi()
    {
        bool animated = _pictureBox.IsAnimated;
        _toolbar.PlayButton.Enabled = animated && !_busy;

        if (!animated)
        {
            _toolbar.PlayButton.Text = "动画";
            _toolbar.PlayButton.ToolTipText = "当前图片不是动画";
            if (!_busy)
            {
                _statusbar.FrameLabel.Text = string.Empty;
            }

            return;
        }

        _toolbar.PlayButton.Text = _pictureBox.IsPlaying ? "⏸ 暂停" : "▶ 播放";
        _toolbar.PlayButton.ToolTipText = "播放 / 暂停动画 (空格)，逐帧查看用 , 和 .";
        UpdateFrameUi(_pictureBox.CurrentFrameIndex);
    }

    private void UpdateFrameUi(int index)
    {
        if (_busy || !_pictureBox.IsAnimated)
        {
            return;
        }

        string state = _pictureBox.IsPlaying ? "▶" : "⏸";
        _statusbar.FrameLabel.Text = $"{state} {index + 1} / {_current?.FrameCount ?? 0}";
    }

    private void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _restoreBorderStyle = FormBorderStyle;
            _restoreBounds = Bounds;
            _thumbsVisibleBeforeFullScreen = _thumbnails.Visible;
            _infoVisibleBeforeFullScreen = _infoPanel.Visible;
            _pictureBackColor = _pictureBox.BackColor;

            _menuStrip.Visible = false;
            _toolbar.Strip.Visible = false;
            _statusbar.Strip.Visible = false;
            _thumbnails.Visible = false;
            _infoPanel.Visible = false;

            // 图片覆盖不到的地方用黑色显示
            _pictureBox.BackColor = Color.Black;

            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            _fullScreen = true;
        }
        else
        {
            WindowState = FormWindowState.Normal;
            FormBorderStyle = _restoreBorderStyle;
            Bounds = _restoreBounds;

            _pictureBox.BackColor = _pictureBackColor;

            _menuStrip.Visible = true;
            _toolbar.Strip.Visible = true;
            _statusbar.Strip.Visible = true;
            _thumbnails.Visible = _thumbsVisibleBeforeFullScreen;
            _infoPanel.Visible = _infoVisibleBeforeFullScreen;

            _fullScreen = false;
        }

        FocusPicture();
    }

    private void FocusPicture()
    {
        if (!_pictureBox.Focused)
        {
            _pictureBox.Focus();
        }
    }

    /// <summary>根据加载状态与图片情况刷新按钮可用状态与序号显示。</summary>
    private void UpdateUiState()
    {
        bool hasImage = _pictureBox.HasImage && !_busy;
        bool canNavigate = hasImage && _files.Count > 1;

        _toolbar.PrevButton.Enabled = canNavigate;
        _toolbar.NextButton.Enabled = canNavigate;
        _toolbar.ZoomInButton.Enabled = hasImage;
        _toolbar.ZoomOutButton.Enabled = hasImage;
        _toolbar.FitButton.Enabled = hasImage;
        _toolbar.ActualButton.Enabled = hasImage;
        _toolbar.RotateLeftButton.Enabled = hasImage;
        _toolbar.RotateRightButton.Enabled = hasImage;
        _toolbar.PlayButton.Enabled = hasImage && _pictureBox.IsAnimated;
        _toolbar.ExportFramesButton.Enabled = hasImage && _pictureBox.IsAnimated;
        _toolbar.NavButton.Enabled = !_busy && _files.Count > 0;
        _toolbar.InfoButton.Enabled = hasImage;

        _toolbar.RedButton.Enabled = hasImage;
        _toolbar.GreenButton.Enabled = hasImage;
        _toolbar.BlueButton.Enabled = hasImage;
        _toolbar.AlphaButton.Enabled = hasImage;
        _toolbar.FullButton.Enabled = hasImage;

        _statusbar.IndexLabel.Text = canNavigate ? $"{_currentIndex + 1} / {_files.Count}" : string.Empty;
    }

    private static int IndexOf(IReadOnlyList<string> files, string path)
    {
        for (int i = 0; i < files.Count; i++)
        {
            if (string.Equals(files[i], path, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>工具栏及其按钮集合。</summary>
    private sealed class Toolbar
    {
        public ToolStrip Strip { get; }

        public ToolStripButton NavButton { get; }
        public ToolStripButton PrevButton { get; }
        public ToolStripButton NextButton { get; }
        public ToolStripButton ZoomInButton { get; }
        public ToolStripButton ZoomOutButton { get; }
        public ToolStripButton FitButton { get; }
        public ToolStripButton ActualButton { get; }
        public ToolStripButton RotateLeftButton { get; }
        public ToolStripButton RotateRightButton { get; }
        public ToolStripButton PlayButton { get; }
        public ToolStripButton ExportFramesButton { get; }
        public ToolStripButton InfoButton { get; }

        // 通道分离显示：R / G / B / A(棋盘格) / 完整
        public ToolStripButton RedButton { get; }
        public ToolStripButton GreenButton { get; }
        public ToolStripButton BlueButton { get; }
        public ToolStripButton AlphaButton { get; }
        public ToolStripButton FullButton { get; }

        public Toolbar()
        {
            NavButton = CreateButton("导航", "显示 / 隐藏缩略图导航栏");
            PrevButton = CreateButton("◀ 上一张", "上一张 (←)");
            NextButton = CreateButton("下一张 ▶", "下一张 (→)");
            ZoomInButton = CreateButton("放大 +", "放大 (+)");
            ZoomOutButton = CreateButton("缩小 −", "缩小 (-)");
            FitButton = CreateButton("适应窗口", "适应窗口 (0)");
            ActualButton = CreateButton("1:1", "实际大小 (1)");
            RotateLeftButton = CreateButton("↺ 左旋", "逆时针旋转 90° (Ctrl+←)");
            RotateRightButton = CreateButton("↻ 右旋", "顺时针旋转 90° (Ctrl+→)");
            PlayButton = CreateButton("动画", "播放 / 暂停动画 (空格)");
            ExportFramesButton = CreateButton("导出序列帧", "把动态图导出为 PNG 序列帧到文件夹");
            InfoButton = CreateButton("信息", "显示 / 隐藏图片信息面板（EXIF 等）");

            RedButton = CreateChannelButton(SolidIcon(Color.FromArgb(229, 57, 53)), "只显示红色通道 (R)");
            GreenButton = CreateChannelButton(SolidIcon(Color.FromArgb(67, 160, 71)), "只显示绿色通道 (G)");
            BlueButton = CreateChannelButton(SolidIcon(Color.FromArgb(30, 136, 229)), "只显示蓝色通道 (B)");
            AlphaButton = CreateChannelButton(CheckerIcon(), "以棋盘格显示透明/Alpha 通道 (A)");
            FullButton = CreateChannelButton(RgbIcon(), "完整显示所有通道");

            Strip = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                RenderMode = ToolStripRenderMode.System,
                Padding = new Padding(6, 2, 6, 2)
            };

            Strip.Items.AddRange(new ToolStripItem[]
            {
                NavButton,
                new ToolStripSeparator(),
                PrevButton,
                NextButton,
                new ToolStripSeparator(),
                ZoomInButton,
                ZoomOutButton,
                FitButton,
                ActualButton,
                new ToolStripSeparator(),
                RotateLeftButton,
                RotateRightButton,
                new ToolStripSeparator(),
                PlayButton,
                ExportFramesButton,
                new ToolStripSeparator(),
                InfoButton,
                new ToolStripSeparator(),
                RedButton,
                GreenButton,
                BlueButton,
                AlphaButton,
                FullButton
            });

            UpdateChannelButtons(ChannelMode.Full);
        }

        /// <summary>按当前通道模式设置 5 个按钮的选中态（单选）。</summary>
        public void UpdateChannelButtons(ChannelMode mode)
        {
            RedButton.Checked = mode == ChannelMode.Red;
            GreenButton.Checked = mode == ChannelMode.Green;
            BlueButton.Checked = mode == ChannelMode.Blue;
            AlphaButton.Checked = mode == ChannelMode.Alpha;
            FullButton.Checked = mode == ChannelMode.Full;
        }

        private static ToolStripButton CreateButton(string text, string toolTip) => new(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = toolTip
        };

        private static ToolStripButton CreateChannelButton(Image icon, string toolTip) => new()
        {
            Image = icon,
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ToolTipText = toolTip,
            AutoToolTip = true
        };

        private static Bitmap SolidIcon(Color color)
        {
            var bmp = new Bitmap(16, 16);
            using Graphics g = Graphics.FromImage(bmp);
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, 1, 1, 14, 14);
            return bmp;
        }

        private static Bitmap CheckerIcon()
        {
            var bmp = new Bitmap(16, 16);
            using Graphics g = Graphics.FromImage(bmp);
            using var light = new SolidBrush(Color.White);
            using var dark = new SolidBrush(Color.FromArgb(150, 150, 150));
            g.FillRectangle(light, 0, 0, 16, 16);
            g.FillRectangle(dark, 0, 0, 8, 8);
            g.FillRectangle(dark, 8, 8, 8, 8);
            return bmp;
        }

        private static Bitmap RgbIcon()
        {
            var bmp = new Bitmap(16, 16);
            using Graphics g = Graphics.FromImage(bmp);
            using var r = new SolidBrush(Color.FromArgb(229, 57, 53));
            using var gr = new SolidBrush(Color.FromArgb(67, 160, 71));
            using var b = new SolidBrush(Color.FromArgb(30, 136, 229));
            g.FillRectangle(r, 1, 1, 4, 14);
            g.FillRectangle(gr, 6, 1, 4, 14);
            g.FillRectangle(b, 11, 1, 4, 14);
            return bmp;
        }
    }

    /// <summary>状态栏及其标签集合。</summary>
    private sealed class Statusbar
    {
        public StatusStrip Strip { get; }

        public ToolStripStatusLabel PathLabel { get; }
        public ToolStripStatusLabel InfoLabel { get; }
        public ToolStripStatusLabel WarningLabel { get; }
        public ToolStripStatusLabel FrameLabel { get; }
        public ToolStripStatusLabel IndexLabel { get; }
        public ToolStripStatusLabel ZoomLabel { get; }

        public Statusbar()
        {
            PathLabel = new ToolStripStatusLabel("未打开图片")
            {
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Overflow = ToolStripItemOverflow.Never,
                AutoToolTip = true
            };

            InfoLabel = CreateStatusLabel();
            WarningLabel = CreateStatusLabel();
            WarningLabel.Text = "⚠";
            WarningLabel.Visible = false;
            FrameLabel = CreateStatusLabel();
            IndexLabel = CreateStatusLabel();
            ZoomLabel = CreateStatusLabel();

            Strip = new StatusStrip { SizingGrip = false };
            Strip.Items.AddRange(new ToolStripItem[]
            {
                PathLabel, WarningLabel, InfoLabel, FrameLabel, IndexLabel, ZoomLabel
            });
        }

        public void SetPath(string path)
        {
            PathLabel.Text = path;
            PathLabel.ToolTipText = path;
        }

        private static ToolStripStatusLabel CreateStatusLabel() => new(string.Empty)
        {
            TextAlign = ContentAlignment.MiddleRight,
            BorderSides = ToolStripStatusLabelBorderSides.Left
        };
    }
}
