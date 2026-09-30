using System.Drawing.Imaging;

namespace WinFormsApp1;

/// <summary>
/// 动画播放方式。
/// </summary>
public enum AnimationKind
{
    /// <summary>静态图片，不需要播放。</summary>
    None,

    /// <summary>交给 GDI+ 的 <see cref="ImageAnimator"/> 播放（GIF 等，内存占用与帧数无关）。</summary>
    GdiAnimator,

    /// <summary>已解码为位图帧序列，由控件内部计时器逐帧播放（WebP / JXL 动画等）。</summary>
    FrameSequence
}

/// <summary>
/// 解码后的图片：可能是单帧静态图，也可能是多帧动画。负责持有并释放所有位图资源。
/// 对 SVG 这类矢量格式，还保留了源数据，可按需以更高分辨率重新栅格化。
/// </summary>
public sealed class DecodedImage : IDisposable
{
    /// <summary>矢量图重新栅格化时允许的最大宽度（像素），防止无限放大耗尽内存。</summary>
    public const int MaxRasterWidth = 8192;

    private Bitmap[] _frames;
    private readonly int[] _delaysMs;
    private readonly byte[]? _vectorBytes;
    private readonly object _renderSync = new();
    private bool _disposed;

    private DecodedImage(string filePath, string formatName, AnimationKind kind, Image? gdiImage,
        Bitmap[] frames, int[] delaysMs, int sourceFrameCount, int logicalWidth, int logicalHeight,
        byte[]? vectorBytes, IReadOnlyList<string>? warnings)
    {
        FilePath = filePath;
        FormatName = formatName;
        AnimationKind = kind;
        GdiImage = gdiImage;
        _frames = frames;
        _delaysMs = delaysMs;
        _vectorBytes = vectorBytes;
        SourceFrameCount = sourceFrameCount;
        Warnings = warnings ?? Array.Empty<string>();
        Width = logicalWidth;
        Height = logicalHeight;

        FrameCount = kind switch
        {
            AnimationKind.GdiAnimator => Math.Max(1, sourceFrameCount),
            AnimationKind.FrameSequence => frames.Length,
            _ => 1
        };

        IsAnimated = kind != AnimationKind.None && FrameCount > 1;
        FramesTruncated = kind == AnimationKind.FrameSequence && sourceFrameCount > frames.Length;
    }

    /// <summary>源文件路径。</summary>
    public string FilePath { get; }

    /// <summary>实际解码出的格式名称，例如 PSD / HEIC / SVG。</summary>
    public string FormatName { get; }

    /// <summary>
    /// 图片的逻辑宽度（像素）。对位图等于帧宽度；对 SVG 是 96dpi 下的原始尺寸，
    /// 与当前栅格化分辨率无关，因此重新栅格化不会改变显示大小。
    /// </summary>
    public int Width { get; }

    /// <summary>图片的逻辑高度（像素），含义同 <see cref="Width"/>。</summary>
    public int Height { get; }

    /// <summary>动画播放方式。</summary>
    public AnimationKind AnimationKind { get; }

    /// <summary>
    /// 由 GDI+ 直接持有的图片，仅当 <see cref="AnimationKind"/> 为 <see cref="AnimationKind.GdiAnimator"/>
    /// 或 <see cref="AnimationKind.None"/>（GIF / WMF / EMF）时非空。
    /// </summary>
    public Image? GdiImage { get; }

    /// <summary>可用于播放的帧数。</summary>
    public int FrameCount { get; }

    /// <summary>文件中的原始帧数，帧数被裁剪时会大于 <see cref="FrameCount"/>。</summary>
    public int SourceFrameCount { get; }

    /// <summary>是否为动画。</summary>
    public bool IsAnimated { get; }

    /// <summary>帧数是否因超出内存/数量上限而被裁剪。</summary>
    public bool FramesTruncated { get; }

    /// <summary>解码过程中的提示信息（例如 SVG 栅格化说明、帧数裁剪说明）。</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>是否为矢量图，可以按需重新栅格化。</summary>
    public bool CanRerender => _vectorBytes is not null;

    /// <summary>当前栅格化结果的宽度（像素），非矢量图等于 <see cref="Width"/>。</summary>
    public int CurrentRasterWidth
    {
        get
        {
            lock (_renderSync)
            {
                return _frames.Length > 0 ? _frames[0].Width : Width;
            }
        }
    }

    /// <summary>当前栅格化分辨率相对逻辑尺寸的倍率。</summary>
    public double RasterScale => Width <= 0 ? 1.0 : (double)CurrentRasterWidth / Width;

    /// <summary>
    /// 估算的位图内存占用（字节），用于 <see cref="ImageCache"/> 的内存预算与淘汰。
    /// 按每像素 4 字节估算；GDI+ 动画按帧数放大。
    /// </summary>
    public long EstimatedBytes
    {
        get
        {
            lock (_renderSync)
            {
                long bytes = 0;
                foreach (Bitmap frame in _frames)
                {
                    bytes += (long)Math.Max(1, frame.Width) * Math.Max(1, frame.Height) * 4;
                }

                if (GdiImage is not null)
                {
                    bytes += (long)Math.Max(1, GdiImage.Width) * Math.Max(1, GdiImage.Height) * 4 * Math.Max(1, FrameCount);
                }

                return Math.Max(bytes, (long)Math.Max(1, Width) * Math.Max(1, Height) * 4);
            }
        }
    }

    /// <summary>矢量图重新栅格化完成后触发，必须在 UI 线程订阅。</summary>
    public event Action? RasterUpdated;

    /// <summary>创建由 GDI+ 持有的图片（GIF 动画或 WMF/EMF）。</summary>
    public static DecodedImage FromGdiImage(string filePath, string formatName, Image image, IReadOnlyList<string>? warnings = null)
    {
        int frameCount = CountGdiFrames(image);
        AnimationKind kind = frameCount > 1 ? AnimationKind.GdiAnimator : AnimationKind.None;
        return new DecodedImage(filePath, formatName, kind, image, Array.Empty<Bitmap>(), Array.Empty<int>(),
            frameCount, image.Width, image.Height, null, warnings);
    }

    /// <summary>创建位图帧序列（静态图为长度 1 的序列）。</summary>
    public static DecodedImage FromFrames(string filePath, string formatName, Bitmap[] frames, int[] delaysMs,
        int sourceFrameCount, IReadOnlyList<string>? warnings = null)
    {
        if (frames.Length == 0)
        {
            throw new ArgumentException("帧序列不能为空。", nameof(frames));
        }

        AnimationKind kind = frames.Length > 1 ? AnimationKind.FrameSequence : AnimationKind.None;
        return new DecodedImage(filePath, formatName, kind, null, frames, delaysMs, sourceFrameCount,
            frames[0].Width, frames[0].Height, null, warnings);
    }

    /// <summary>
    /// 创建矢量图（SVG）。<paramref name="raster"/> 是按 <paramref name="rasterScale"/> 倍率渲染出的首帧，
    /// 逻辑尺寸由此反推，后续可调用 <see cref="RerenderAsync"/> 提升分辨率。
    /// </summary>
    public static DecodedImage FromSvg(string filePath, string formatName, byte[] svgBytes, Bitmap raster,
        double rasterScale, IReadOnlyList<string>? warnings = null)
    {
        if (rasterScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rasterScale));
        }

        int logicalWidth = Math.Max(1, (int)Math.Round(raster.Width / rasterScale));
        int logicalHeight = Math.Max(1, (int)Math.Round(raster.Height / rasterScale));

        return new DecodedImage(filePath, formatName, AnimationKind.None, null, new[] { raster }, new[] { 0 },
            1, logicalWidth, logicalHeight, svgBytes, warnings);
    }

    /// <summary>取得用于绘制的图片。</summary>
    /// <param name="index">帧序号；GDI+ 动画模式下帧推进由 <see cref="ImageAnimator"/> 负责，返回同一个对象。</param>
    public Image GetFrame(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (GdiImage is not null)
        {
            return GdiImage;
        }

        lock (_renderSync)
        {
            if (_frames.Length == 0)
            {
                throw new InvalidOperationException("没有可用的图片帧。");
            }

            return _frames[Math.Clamp(index, 0, _frames.Length - 1)];
        }
    }

    /// <summary>取得指定帧的显示时长（毫秒），静态图返回 0。</summary>
    public int GetDelayMs(int index)
    {
        if (_delaysMs.Length == 0)
        {
            return 0;
        }

        // 部分文件会把延迟写成 0，按浏览器惯例回退到 100ms，避免动画卡死
        int delay = _delaysMs[Math.Clamp(index, 0, _delaysMs.Length - 1)];
        return delay < 20 ? 100 : delay;
    }

    /// <summary>
    /// 按目标像素宽度重新栅格化矢量图。只升不降：目标宽度不大于当前分辨率时直接返回 false。
    /// 必须在 UI 线程调用，替换帧的动作发生在 UI 线程，不会与绘制并发。
    /// </summary>
    /// <param name="targetPixelWidth">期望的位图宽度（像素）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否真的换上了更高分辨率的位图。</returns>
    public async Task<bool> RerenderAsync(int targetPixelWidth, CancellationToken cancellationToken = default)
    {
        if (_vectorBytes is null || _disposed || Width <= 0)
        {
            return false;
        }

        int target = Math.Clamp(targetPixelWidth, 1, MaxRasterWidth);
        if (target <= CurrentRasterWidth)
        {
            return false;
        }

        byte[] source = _vectorBytes;
        double scale = (double)target / Width;

        Bitmap fresh = await Task.Run(() => ImageLoader.RasterizeSvg(source, scale), cancellationToken)
            .ConfigureAwait(true);

        lock (_renderSync)
        {
            if (_disposed)
            {
                fresh.Dispose();
                return false;
            }

            Bitmap old = _frames[0];
            _frames[0] = fresh;
            old.Dispose();
        }

        RasterUpdated?.Invoke();
        return true;
    }

    /// <summary>生成状态栏展示用的描述文本。</summary>
    public string Describe()
    {
        string size = $"{Width} × {Height}";
        if (!IsAnimated)
        {
            string vector = CanRerender ? $"  |  矢量已渲染 {CurrentRasterWidth}px" : string.Empty;
            return $"{size}  |  {FormatName}{vector}";
        }

        string frames = FramesTruncated ? $"动画 {FrameCount}/{SourceFrameCount} 帧" : $"动画 {FrameCount} 帧";
        return $"{size}  |  {FormatName}  |  {frames}";
    }

    public void Dispose()
    {
        lock (_renderSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (Bitmap frame in _frames)
            {
                frame.Dispose();
            }

            _frames = Array.Empty<Bitmap>();
        }

        GdiImage?.Dispose();
    }

    /// <summary>统计 GDI+ 图片的时间维度帧数，失败时按单帧处理。</summary>
    private static int CountGdiFrames(Image image)
    {
        try
        {
            return image.GetFrameCount(FrameDimension.Time);
        }
        catch
        {
            return 1;
        }
    }
}
