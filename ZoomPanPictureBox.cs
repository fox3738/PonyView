using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace WinFormsApp1;

/// <summary>通道分离显示模式。</summary>
public enum ChannelMode
{
    /// <summary>完整显示所有通道（正常合成）。</summary>
    Full,

    /// <summary>只显示红色通道。</summary>
    Red,

    /// <summary>只显示绿色通道。</summary>
    Green,

    /// <summary>只显示蓝色通道。</summary>
    Blue,

    /// <summary>以黑白灰度显示 Alpha（透明）通道。</summary>
    Alpha
}

/// <summary>
/// 支持鼠标滚轮缩放（以光标为中心）、拖拽平移、旋转、适应窗口，
/// 并能播放 GIF / WebP / APNG / JXL 等多帧动画的图片显示控件。
/// </summary>
public sealed class ZoomPanPictureBox : Control
{
    /// <summary>最小缩放倍率。</summary>
    public const float MinZoom = 0.02f;

    /// <summary>最大缩放倍率。</summary>
    public const float MaxZoom = 64f;

    private const float ZoomStep = 1.25f;
    private const int FitMargin = 16;
    private const int MinFrameInterval = 10;

    /// <summary>矢量图重栅格化的防抖时长（毫秒），避免滚轮连续缩放时反复渲染。</summary>
    private const int RerenderDebounceMs = 220;

    /// <summary>交互（缩放/平移）停止后补渲染高质量帧的防抖时长（毫秒）。</summary>
    private const int QualitySettleMs = 120;

    private readonly System.Windows.Forms.Timer _animationTimer;
    private readonly System.Windows.Forms.Timer _rerenderTimer;
    private CancellationTokenSource? _rerenderCts;
    private int _pendingRasterWidth;
    private bool _rerendering;

    private DecodedImage? _image;
    private float _zoom = 1f;
    private PointF _offset; // 图片（含旋转后的外接矩形）左上角在控件坐标中的位置
    private int _rotation;  // 0 / 90 / 180 / 270
    private bool _isFitMode = true;
    private bool _isPanning;
    private Point _lastMouse;
    private int _frameIndex;
    private ChannelMode _channelMode = ChannelMode.Full;
    private Bitmap? _checkerTile;
    private TextureBrush? _checkerBrush;
    private ImageAttributes? _channelAttrs;

    // 交互（缩放/平移）期间降低渲染质量换取流畅，停止后由 _qualityTimer 补一帧高质量
    private bool _interacting;
    private readonly System.Windows.Forms.Timer _qualityTimer;

    /// <summary>缩放倍率发生变化时触发，参数为新的倍率。</summary>
    public event Action<float>? ZoomChanged;

    /// <summary>动画播放/暂停状态变化时触发。</summary>
    public event Action? PlaybackChanged;

    /// <summary>当前帧序号变化时触发（仅帧序列动画）。</summary>
    public event Action<int>? FrameChanged;

    public ZoomPanPictureBox()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable |
            ControlStyles.SupportsTransparentBackColor,
            true);
        UpdateStyles();

        DoubleBuffered = true;
        BackColor = Color.FromArgb(45, 45, 48);
        TabStop = true;
        Cursor = Cursors.Default;
        Dock = DockStyle.Fill;

        _animationTimer = new System.Windows.Forms.Timer();
        _animationTimer.Tick += OnAnimationTick;

        _rerenderTimer = new System.Windows.Forms.Timer { Interval = RerenderDebounceMs };
        _rerenderTimer.Tick += OnRerenderTick;

        _qualityTimer = new System.Windows.Forms.Timer { Interval = QualitySettleMs };
        _qualityTimer.Tick += OnQualityTimerTick;
    }

    /// <summary>当前显示的图片，由调用方负责释放。</summary>
    public DecodedImage? Picture => _image;

    /// <summary>用于绘制的当前帧。</summary>
    public Image? CurrentFrame => _image?.GetFrame(_frameIndex);

    /// <summary>当前缩放倍率，1 表示 100%。</summary>
    public float Zoom => _zoom;

    /// <summary>当前旋转角度（顺时针），取值为 0 / 90 / 180 / 270。</summary>
    public int Rotation => _rotation;

    /// <summary>是否处于“适应窗口”模式，该模式下窗口大小变化会自动重新适配。</summary>
    public bool IsFitMode => _isFitMode;

    /// <summary>是否已加载图片。</summary>
    public bool HasImage => _image is not null;

    /// <summary>当前图片是否为动画。</summary>
    public bool IsAnimated => _image?.IsAnimated == true;

    /// <summary>动画是否正在播放。</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>当前帧序号（从 0 开始）。</summary>
    public int CurrentFrameIndex => _frameIndex;

    /// <summary>当前通道显示模式。</summary>
    public ChannelMode ChannelMode
    {
        get => _channelMode;
        set
        {
            if (_channelMode == value)
            {
                return;
            }

            _channelMode = value;
            ChannelModeChanged?.Invoke(value);
            Invalidate();
        }
    }

    /// <summary>通道显示模式变化时触发。</summary>
    public event Action<ChannelMode>? ChannelModeChanged;

    /// <summary>用户双击图片、请求切换全屏时触发。</summary>
    public event Action? RequestToggleFullScreen;

    /// <summary>设置图片并重置视图（默认适应窗口），动画会自动开始播放。</summary>
    public void SetImage(DecodedImage? image, bool fitToWindow = true)
    {
        CancelPendingRerender();
        StopPlayback();

        _image = image;
        _rotation = 0;
        _frameIndex = 0;
        _isFitMode = fitToWindow;

        if (image is null)
        {
            Invalidate();
            return;
        }

        if (fitToWindow)
        {
            FitToWindow();
        }
        else
        {
            ActualSize();
        }

        StartPlaybackIfAnimated();
        MaybeRequestRerender();
    }

    /// <summary>缩放到适应窗口大小，并保持居中。</summary>
    public void FitToWindow()
    {
        if (_image is null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            _isFitMode = _image is not null;
            return;
        }

        SizeF size = DisplaySize;
        float availableWidth = Math.Max(1, ClientSize.Width - FitMargin * 2);
        float availableHeight = Math.Max(1, ClientSize.Height - FitMargin * 2);

        float fit = Math.Min(availableWidth / size.Width, availableHeight / size.Height);
        _isFitMode = true;

        SetZoom(fit);
        CenterImage();
        RaiseZoomChanged();
        Invalidate();
    }

    /// <summary>以 100% 比例显示图片，并保持居中。</summary>
    public void ActualSize()
    {
        if (_image is null)
        {
            return;
        }

        _isFitMode = false;
        SetZoom(1f);
        CenterImage();
        RaiseZoomChanged();
        Invalidate();
    }

    /// <summary>以窗口中心为基准放大一级。</summary>
    public void ZoomIn() => ZoomAtCenter(ZoomStep);

    /// <summary>以窗口中心为基准缩小一级。</summary>
    public void ZoomOut() => ZoomAtCenter(1f / ZoomStep);

    /// <summary>按指定系数缩放，以窗口中心为不动点。</summary>
    public void ZoomBy(float factor) => ZoomAtCenter(factor);

    /// <summary>直接设置缩放倍率，以窗口中心为不动点。</summary>
    public void ZoomTo(float zoom)
    {
        if (_image is null)
        {
            return;
        }

        _isFitMode = false;
        ZoomAround(zoom, Center);
        RaiseZoomChanged();
        Invalidate();
    }

    /// <summary>旋转图片。</summary>
    /// <param name="degrees">旋转角度，正值顺时针，一般传 90 或 -90。</param>
    public void Rotate(int degrees)
    {
        if (_image is null)
        {
            return;
        }

        _rotation = ((_rotation + degrees) % 360 + 360) % 360;

        if (_isFitMode)
        {
            FitToWindow();
        }
        else
        {
            CenterImage();
            Invalidate();
        }
    }

    /// <summary>复位视图：取消旋转并按适应窗口显示。</summary>
    public void ResetView()
    {
        _rotation = 0;
        FitToWindow();
    }

    /// <summary>播放或暂停动画，非动画图片无效果。</summary>
    public void TogglePlayback()
    {
        if (!IsAnimated)
        {
            return;
        }

        if (IsPlaying)
        {
            StopPlayback();
        }
        else
        {
            StartPlaybackIfAnimated();
        }
    }

    /// <summary>暂停动画并前后切换一帧，用于逐帧查看。</summary>
    /// <param name="delta">帧偏移量，一般为 1 或 -1。</param>
    public void StepFrame(int delta)
    {
        if (_image is null || !_image.IsAnimated)
        {
            return;
        }

        if (IsPlaying)
        {
            StopPlayback();
        }

        if (_image.AnimationKind != AnimationKind.FrameSequence)
        {
            return; // GDI+ 动画的帧由 ImageAnimator 内部推进，无法精确定位
        }

        _frameIndex = ((_frameIndex + delta) % _image.FrameCount + _image.FrameCount) % _image.FrameCount;
        FrameChanged?.Invoke(_frameIndex);
        Invalidate();
    }

    /// <summary>把控件坐标转换为图片坐标（不含旋转），无图片时返回 null。</summary>
    public PointF? ControlToImage(Point location)
    {
        if (_image is null || Math.Abs(_zoom) < 1e-6f)
        {
            return null;
        }

        SizeF display = DisplaySize;
        float cx = _offset.X + display.Width * _zoom / 2f;
        float cy = _offset.Y + display.Height * _zoom / 2f;

        double rad = -_rotation * Math.PI / 180.0;
        float dx = location.X - cx;
        float dy = location.Y - cy;
        float rx = (float)(dx * Math.Cos(rad) - dy * Math.Sin(rad));
        float ry = (float)(dx * Math.Sin(rad) + dy * Math.Cos(rad));

        return new PointF(rx / _zoom + _image.Width / 2f, ry / _zoom + _image.Height / 2f);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Graphics g = e.Graphics;

        if (_image is null)
        {
            DrawPlaceholder(g);
            return;
        }

        Image frame;
        try
        {
            // GDI+ 动画：Animate 只负责计时回调，帧的实际推进必须在这里显式调用 UpdateFrames，
            // 否则每次重绘都停留在第 0 帧，表现为“能显示但不动”。
            if (_image.AnimationKind == AnimationKind.GdiAnimator && IsPlaying)
            {
                ImageAnimator.UpdateFrames();
            }

            frame = _image.GetFrame(_frameIndex);
        }
        catch (InvalidOperationException)
        {
            return; // 图片正在被释放（ObjectDisposedException 也属于此类型）
        }

        if (_interacting)
        {
            // 缩放/平移过程中优先流畅：放大用最近邻、缩小用双线性，停下后再补一帧高质量
            g.InterpolationMode = _zoom >= 1f ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            g.CompositingQuality = CompositingQuality.HighSpeed;
        }
        else
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
        }

        SizeF display = DisplaySize;
        float scaledWidth = display.Width * _zoom;
        float scaledHeight = display.Height * _zoom;

        GraphicsState state = g.Save();
        g.TranslateTransform(_offset.X + scaledWidth / 2f, _offset.Y + scaledHeight / 2f);

        if (_rotation != 0)
        {
            g.RotateTransform(_rotation);
        }

        // 目标矩形按逻辑尺寸计算，源矩形按帧的真实像素：
        // SVG 重新栅格化只会提升帧分辨率，显示大小保持不变，画面只是变清晰。
        var destRect = new RectangleF(-display.Width * _zoom / 2f, -display.Height * _zoom / 2f, scaledWidth, scaledHeight);
        var srcRect = new RectangleF(0, 0, frame.Width, frame.Height);

        try
        {
            DrawFrame(g, frame, destRect, srcRect);
        }
        catch (InvalidOperationException)
        {
            // 帧可能在绘制过程中被重栅格化替换掉，忽略这一帧
        }

        g.Restore(state);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        if (_image is null)
        {
            return;
        }

        if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle)
        {
            _isPanning = true;
            _lastMouse = e.Location;
            Cursor = Cursors.SizeAll;
            _isFitMode = false;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_isPanning)
        {
            return;
        }

        _offset = new PointF(_offset.X + (e.X - _lastMouse.X), _offset.Y + (e.Y - _lastMouse.Y));
        _lastMouse = e.Location;
        BeginInteraction();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (_isPanning && (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle))
        {
            _isPanning = false;
            Cursor = HasImage ? Cursors.Hand : Cursors.Default;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        if (_image is null)
        {
            return;
        }

        float factor = e.Delta > 0 ? ZoomStep : 1f / ZoomStep;
        ZoomAtCursor(factor, e.Location);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);

        // 双击切换全屏（进入 / 退出）
        if (e.Button == MouseButtons.Left)
        {
            RequestToggleFullScreen?.Invoke();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        if (_image is null)
        {
            Invalidate();
            return;
        }

        if (_isFitMode)
        {
            FitToWindow();
        }
        else
        {
            CenterImage();
            Invalidate();
        }

        MaybeRequestRerender();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);

        Cursor = HasImage ? Cursors.Hand : Cursors.Default;

        // 鼠标移入时获取焦点，使滚轮缩放无需先点击即可使用
        if (!Focused && CanFocus)
        {
            Focus();
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        CancelPendingRerender();
        StopPlayback();
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelPendingRerender();
            StopPlayback();
            _animationTimer.Dispose();
            _rerenderTimer.Dispose();
            _qualityTimer.Dispose();
            _rerenderCts?.Dispose();
            _channelAttrs?.Dispose();
            _channelAttrs = null;
            _checkerBrush?.Dispose();
            _checkerBrush = null;
            _checkerTile?.Dispose();
            _checkerTile = null;
        }

        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- 通道分离渲染

    /// <summary>按当前 <see cref="ChannelMode"/> 绘制帧：各通道（R/G/B/A）视图均恒为不透明，只呈现该通道的原始数值；仅完整显示（Full）模式先垫棋盘格以露出透明区域。</summary>
    private void DrawFrame(Graphics g, Image frame, RectangleF destRect, RectangleF srcRect)
    {
        Rectangle dest = Rectangle.Round(destRect);
        switch (_channelMode)
        {
            case ChannelMode.Alpha:
                // Alpha 通道以黑白灰度显示：透明度越高越暗，完全不透明处为白，输出恒为不透明
                ChannelAttrs.SetColorMatrix(AlphaMatrix);
                g.DrawImage(frame, dest, srcRect.X, srcRect.Y, srcRect.Width, srcRect.Height,
                    GraphicsUnit.Pixel, ChannelAttrs);
                break;

            case ChannelMode.Red:
            case ChannelMode.Green:
            case ChannelMode.Blue:
                // 通道视图恒为不透明：只呈现该通道的原始数值，不垫棋盘格、也不再应用源 Alpha
                ChannelAttrs.SetColorMatrix(ChannelColorMatrix(_channelMode));
                g.DrawImage(frame, dest, srcRect.X, srcRect.Y, srcRect.Width, srcRect.Height,
                    GraphicsUnit.Pixel, ChannelAttrs);
                break;

            default:
                // 完整显示：先垫棋盘格（透明背景露出棋盘格），再正常合成绘制
                FillChecker(g, destRect);
                g.DrawImage(frame, destRect, srcRect, GraphicsUnit.Pixel);
                break;
        }
    }

    /// <summary>用棋盘格填满指定矩形，作为透明区域的背景。</summary>
    private void FillChecker(Graphics g, RectangleF rect)
    {
        g.FillRectangle(CheckerBrush, rect);
    }

    /// <summary>复用的棋盘格画刷（惰性创建并缓存），避免每次绘制都新建。</summary>
    private TextureBrush CheckerBrush => _checkerBrush ??= new TextureBrush(CheckerTile) { WrapMode = WrapMode.Tile };

    /// <summary>复用的 ImageAttributes（仅 UI 线程绘制时使用），避免每帧分配。</summary>
    private ImageAttributes ChannelAttrs => _channelAttrs ??= new ImageAttributes();

    // 颜色矩阵不可变且构造有开销，按模式缓存为静态实例，避免每次绘制都新建
    private static readonly ColorMatrix RedMatrix = BuildChannelMatrix(ChannelMode.Red);
    private static readonly ColorMatrix GreenMatrix = BuildChannelMatrix(ChannelMode.Green);
    private static readonly ColorMatrix BlueMatrix = BuildChannelMatrix(ChannelMode.Blue);
    private static readonly ColorMatrix AlphaMatrix = BuildAlphaMatrix();

    /// <summary>取指定颜色通道对应的缓存颜色矩阵。</summary>
    private static ColorMatrix ChannelColorMatrix(ChannelMode mode) => mode switch
    {
        ChannelMode.Red => RedMatrix,
        ChannelMode.Green => GreenMatrix,
        ChannelMode.Blue => BlueMatrix,
        _ => AlphaMatrix
    };

    /// <summary>把指定颜色通道单独保留、其余通道置零的颜色矩阵（丢弃源 Alpha，输出恒为不透明）。</summary>
    private static ColorMatrix BuildChannelMatrix(ChannelMode mode)
    {
        float r = mode == ChannelMode.Red ? 1f : 0f;
        float gr = mode == ChannelMode.Green ? 1f : 0f;
        float b = mode == ChannelMode.Blue ? 1f : 0f;

        // GDI+ 颜色矩阵约定：行 = 输入分量(R,G,B,A,常量1)，列 = 输出分量(R',G',B',A',W')
        return new ColorMatrix(new[]
        {
            new[] { r, 0f, 0f, 0f, 0f },
            new[] { 0f, gr, 0f, 0f, 0f },
            new[] { 0f, 0f, b, 0f, 0f },
            new[] { 0f, 0f, 0f, 0f, 0f }, // 源 Alpha 不再参与输出：查看 RGB 通道时不应应用 Alpha
            new[] { 0f, 0f, 0f, 1f, 0f }  // 常量 1 → 输出 Alpha 恒为 1（不透明），与 Alpha 通道模式写法一致
        });
    }

    /// <summary>把 Alpha 通道映射为灰度的颜色矩阵：输出的 R/G/B 均取自源 Alpha，并强制输出完全不透明。</summary>
    private static ColorMatrix BuildAlphaMatrix()
    {
        return new ColorMatrix(new[]
        {
            new[] { 0f, 0f, 0f, 0f, 0f },
            new[] { 0f, 0f, 0f, 0f, 0f },
            new[] { 0f, 0f, 0f, 0f, 0f },
            new[] { 1f, 1f, 1f, 0f, 0f }, // 源 Alpha → 输出 R/G/B
            new[] { 0f, 0f, 0f, 1f, 0f }  // 输出 Alpha 恒为 1（不透明）
        });
    }

    /// <summary>棋盘格贴图（惰性创建并缓存），用于透明区域的背景显示。</summary>
    private Bitmap CheckerTile => _checkerTile ??= CreateCheckerTile();

    private static Bitmap CreateCheckerTile()
    {
        const int cell = 8;
        var tile = new Bitmap(cell * 2, cell * 2);
        using Graphics g = Graphics.FromImage(tile);
        using var light = new SolidBrush(Color.FromArgb(255, 255, 255));
        using var dark = new SolidBrush(Color.FromArgb(190, 190, 190));

        g.FillRectangle(light, 0, 0, cell * 2, cell * 2);
        g.FillRectangle(dark, 0, 0, cell, cell);
        g.FillRectangle(dark, cell, cell, cell, cell);
        return tile;
    }

    // ---------------------------------------------------------------- 动画播放

    private void StartPlaybackIfAnimated()
    {
        if (_image is not { IsAnimated: true })
        {
            return;
        }

        if (_image.AnimationKind == AnimationKind.GdiAnimator && _image.GdiImage is not null)
        {
            // ImageAnimator 的回调发生在线程池线程，需要 BeginInvoke 回到 UI 线程
            ImageAnimator.Animate(_image.GdiImage, OnAnimatorFrameChanged);
            IsPlaying = true;
        }
        else if (_image.AnimationKind == AnimationKind.FrameSequence)
        {
            _animationTimer.Interval = Math.Max(MinFrameInterval, _image.GetDelayMs(_frameIndex));
            _animationTimer.Start();
            IsPlaying = true;
        }

        PlaybackChanged?.Invoke();
    }

    private void StopPlayback()
    {
        bool wasPlaying = IsPlaying;

        _animationTimer.Stop();

        if (_image?.AnimationKind == AnimationKind.GdiAnimator && _image.GdiImage is not null)
        {
            try
            {
                ImageAnimator.StopAnimate(_image.GdiImage, OnAnimatorFrameChanged);
            }
            catch
            {
                // 图片可能已被释放，忽略
            }
        }

        IsPlaying = false;

        if (wasPlaying)
        {
            PlaybackChanged?.Invoke();
        }
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (_image is null || _image.AnimationKind != AnimationKind.FrameSequence)
        {
            _animationTimer.Stop();
            return;
        }

        _frameIndex = (_frameIndex + 1) % _image.FrameCount;
        _animationTimer.Interval = Math.Max(MinFrameInterval, _image.GetDelayMs(_frameIndex));
        FrameChanged?.Invoke(_frameIndex);
        Invalidate();
    }

    private void OnAnimatorFrameChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(Invalidate));
        }
        catch (InvalidOperationException)
        {
            // 控件正在销毁（ObjectDisposedException 也属于此类型），忽略
        }
    }

    // ---------------------------------------------------------------- 缩放/定位

    /// <summary>以指定屏幕点为不动点缩放。</summary>
    private void ZoomAtCursor(float factor, Point anchor)
    {
        if (_image is null)
        {
            return;
        }

        _isFitMode = false;
        ZoomAround(_zoom * factor, anchor);
        RaiseZoomChanged();
        Invalidate();
    }

    private void ZoomAtCenter(float factor)
    {
        if (_image is null)
        {
            return;
        }

        _isFitMode = false;
        ZoomAround(_zoom * factor, Center);
        RaiseZoomChanged();
        Invalidate();
    }

    /// <summary>把缩放倍率设为 targetZoom，同时保持 anchor 点对应的图片位置不动。</summary>
    private void ZoomAround(float targetZoom, PointF anchor)
    {
        float newZoom = Math.Clamp(targetZoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 1e-6f)
        {
            return;
        }

        float imageX = (anchor.X - _offset.X) / _zoom;
        float imageY = (anchor.Y - _offset.Y) / _zoom;
        _zoom = newZoom;
        _offset = new PointF(anchor.X - imageX * _zoom, anchor.Y - imageY * _zoom);
        BeginInteraction();
    }

    private void SetZoom(float zoom) => _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);

    /// <summary>标记进入交互状态并重置计时：交互期间用低质量快速渲染，停止后补一帧高质量。</summary>
    private void BeginInteraction()
    {
        _interacting = true;
        _qualityTimer.Stop();
        _qualityTimer.Start();
    }

    private void OnQualityTimerTick(object? sender, EventArgs e)
    {
        _qualityTimer.Stop();
        if (_interacting)
        {
            _interacting = false;
            Invalidate(); // 交互停止后补一帧高质量渲染
        }
    }

    private PointF Center => new(ClientSize.Width / 2f, ClientSize.Height / 2f);

    private void RaiseZoomChanged()
    {
        ZoomChanged?.Invoke(_zoom);
        MaybeRequestRerender();
    }

    // ---------------------------------------------------------------- 矢量图重栅格化

    /// <summary>
    /// 当屏幕上的显示宽度超过当前栅格化分辨率时，安排一次防抖的重新渲染。
    /// 只对 SVG 这类矢量图有效，位图直接返回。
    /// </summary>
    private void MaybeRequestRerender()
    {
        if (_image is not { CanRerender: true } || _image.Width <= 0)
        {
            return;
        }

        float dpiScale = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
        int needed = (int)Math.Ceiling(_image.Width * _zoom * dpiScale);
        needed = Math.Min(needed, DecodedImage.MaxRasterWidth);

        if (needed <= _image.CurrentRasterWidth)
        {
            _rerenderTimer.Stop();
            return;
        }

        _pendingRasterWidth = needed;
        _rerenderTimer.Stop();
        _rerenderTimer.Start();
    }

    private async void OnRerenderTick(object? sender, EventArgs e)
    {
        _rerenderTimer.Stop();

        DecodedImage? image = _image;
        if (image is null || !image.CanRerender || _rerendering)
        {
            return;
        }

        int target = _pendingRasterWidth;
        if (target <= image.CurrentRasterWidth)
        {
            return;
        }

        _rerendering = true;
        _rerenderCts = new CancellationTokenSource();

        try
        {
            // 渲染在后台线程进行，完成后回到 UI 线程替换帧，期间界面仍可正常拖拽缩放
            if (await image.RerenderAsync(target, _rerenderCts.Token))
            {
                Invalidate();
            }
        }
        catch (OperationCanceledException)
        {
            // 切换图片或控件销毁，正常路径
        }
        catch (Exception)
        {
            // 渲染失败时继续使用当前分辨率的位图，不打断浏览
        }
        finally
        {
            _rerendering = false;
            _rerenderCts.Dispose();
            _rerenderCts = null;
        }

        MaybeRequestRerender();
    }

    private void CancelPendingRerender()
    {
        _rerenderTimer.Stop();
        _pendingRasterWidth = 0;

        try
        {
            _rerenderCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略
        }
    }

    private void CenterImage()
    {
        SizeF display = DisplaySize;
        _offset = new PointF(
            (ClientSize.Width - display.Width * _zoom) / 2f,
            (ClientSize.Height - display.Height * _zoom) / 2f);
    }

    /// <summary>考虑旋转后图片实际占用的尺寸。</summary>
    private SizeF DisplaySize
    {
        get
        {
            if (_image is null)
            {
                return SizeF.Empty;
            }

            return _rotation % 180 == 0
                ? new SizeF(_image.Width, _image.Height)
                : new SizeF(_image.Height, _image.Width);
        }
    }

    private void DrawPlaceholder(Graphics g)
    {
        const string text = "将图片拖拽到此处，或按 Ctrl+O 打开图片";

        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var font = new Font("Microsoft YaHei UI", 12f, FontStyle.Regular);
        SizeF size = g.MeasureString(text, font);
        using var brush = new SolidBrush(Color.FromArgb(140, 140, 140));
        g.DrawString(text, font, brush, (ClientSize.Width - size.Width) / 2f, (ClientSize.Height - size.Height) / 2f);
    }
}
