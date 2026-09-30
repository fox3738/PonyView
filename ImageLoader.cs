using System.Diagnostics.CodeAnalysis;
using ImageMagick;

namespace WinFormsApp1;

/// <summary>
/// 图片解码与文件枚举。
/// 常见格式（GIF/WMF/EMF）交给 GDI+，其余格式（PSD/TGA/WebP/HEIC/AVIF/SVG/RAW/JXL/QOI 等）
/// 交给 Magick.NET（ImageMagick），解码结果统一封装为 <see cref="DecodedImage"/>。
/// </summary>
public static class ImageLoader
{
    /// <summary>动画帧数上限，超出部分不播放，避免超大动图耗尽内存。</summary>
    private const int MaxAnimationFrames = 400;

    /// <summary>动画总像素上限（帧数 × 宽 × 高），约对应 800MB 的 32 位位图内存。</summary>
    private const long MaxAnimationPixels = 200_000_000L;

    /// <summary>SVG 首次栅格化的目标宽度下限，保证打开时基本清晰；之后由控件按缩放按需升级。</summary>
    private const int SvgInitialRasterFloor = 1024;

    /// <summary>SVG 栅格化倍率的绝对上限。</summary>
    private const double MaxSvgScale = 8192;

    /// <summary>只能由 GDI+ 解码的图元文件格式。</summary>
    internal static readonly HashSet<string> GdiOnlyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wmf", ".emf"
    };

    /// <summary>GDI+ 能解码的格式，Magick.NET 不可用时用于降级。</summary>
    internal static readonly HashSet<string> GdiCapableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".wmf", ".emf"
    };

    /// <summary>
    /// 可能包含动画帧的格式。PSD/TIFF 的多帧是图层和多页，不能当动画处理。
    /// APNG 虽然格式已注册，但当前构建缺少对应 delegate（实测报 VideoDelegateFailed），
    /// 且多帧写 .png 会被拆成多个文件，因此 PNG 一律按静态图处理。
    /// </summary>
    internal static readonly HashSet<string> AnimatedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".webp", ".jxl"
    };

    /// <summary>相机 RAW 格式，由 dcraw 解码。</summary>
    internal static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".orf",
        ".pef", ".srw", ".rw2", ".x3f", ".rwl", ".mef", ".mos", ".dcr", ".kdc", ".erf", ".mrw", ".raw"
    };

    /// <summary>SVG 矢量格式。</summary>
    internal static readonly HashSet<string> SvgExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svg", ".svgz"
    };

    private static bool? _magickAvailable;

    /// <summary>
    /// 支持的图片扩展名（小写，含点）。
    /// </summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } = new[]
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".wmf", ".emf",
        ".psd", ".psb", ".tga", ".webp", ".heic", ".heif", ".hif", ".avif", ".svg", ".svgz", ".jxl", ".qoi",
        ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".orf",
        ".pef", ".srw", ".rw2", ".x3f", ".rwl", ".mef", ".mos", ".dcr", ".kdc", ".erf", ".mrw", ".raw"
    };

    /// <summary>
    /// 打开对话框使用的过滤器字符串。
    /// </summary>
    public static string DialogFilter { get; } = BuildDialogFilter();

    /// <summary>解码组件（Magick.NET 原生库）是否可用。</summary>
    public static bool IsMagickAvailable => EnsureMagick() == null;

    /// <summary>
    /// 从磁盘加载图片。
    /// </summary>
    /// <param name="path">图片文件路径。</param>
    /// <returns>解码结果，调用方负责 <see cref="DecodedImage.Dispose"/>。</returns>
    /// <exception cref="ArgumentException">路径为空。</exception>
    /// <exception cref="FileNotFoundException">文件不存在。</exception>
    /// <exception cref="ImageDecodeException">解码失败。</exception>
    public static DecodedImage Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("图片路径不能为空。", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("图片文件不存在。", path);
        }

        // 统一入口：交由工厂按扩展名与组件可用性选定具体加载器（策略）
        var request = new ImageLoadRequest(path);
        IImageLoader? loader = ImageLoaderFactory.Resolve(request.Extension);
        if (loader is null)
        {
            string? magickError = EnsureMagick();
            throw new ImageDecodeException(magickError is not null
                ? $"{request.Extension} 需要图片解码组件，但该组件不可用：{magickError}"
                : $"不支持的图片格式：{request.Extension}");
        }

        return loader.Load(request);
    }

    /// <summary>
    /// 尝试加载图片，失败时返回 false 并给出错误信息。
    /// </summary>
    public static bool TryLoad(string path, [NotNullWhen(true)] out DecodedImage? image, out string? error)
    {
        try
        {
            image = Load(path);
            error = null;
            return true;
        }
        catch (ImageDecodeException ex)
        {
            image = null;
            error = ex.Message;
            return false;
        }
        catch (OutOfMemoryException)
        {
            image = null;
            error = "图片尺寸过大或数据已损坏，内存不足。";
            return false;
        }
        catch (Exception ex)
        {
            image = null;
            error = ex.Message;
            return false;
        }
    }

    // ---------------------------------------------------------------- 异步加载 / 内存缓存 / 预加载

    /// <summary>进程级图片内存缓存（LRU + 固定）。</summary>
    private static readonly ImageCache Cache = new();

    /// <summary>同一路径正在进行的解码任务，用于并发去重（避免重复解码）。</summary>
    private static readonly Dictionary<string, Task<DecodedImage>> Inflight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object InflightSync = new();

    /// <summary>
    /// 异步加载图片：命中内存缓存则直接复用，否则在后台线程解码后写入缓存。
    /// 同一路径的并发加载共享同一次解码。返回的实例归缓存所有，调用方不得自行 Dispose。
    /// </summary>
    /// <param name="path">图片路径。</param>
    /// <param name="pin">是否固定（当前显示的图片应固定，防止被淘汰释放）。</param>
    public static async Task<DecodedImage> LoadAsync(string path, bool pin = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("图片路径不能为空。", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("图片文件不存在。", path);
        }

        if (Cache.TryGet(path, out DecodedImage? cached) && cached is not null)
        {
            if (pin)
            {
                Cache.Pin(path);
            }

            return cached;
        }

        Task<DecodedImage> task;
        lock (InflightSync)
        {
            if (!Inflight.TryGetValue(path, out task!))
            {
                task = Task.Run(() => Load(path));
                Inflight[path] = task;
            }
        }

        try
        {
            DecodedImage decoded = await task.ConfigureAwait(false);
            Cache.Commit(path, decoded, pin);
            return decoded;
        }
        finally
        {
            lock (InflightSync)
            {
                Inflight.Remove(path);
            }
        }
    }

    /// <summary>后台预加载图片进缓存（不固定），用于浏览当前图时提前准备相邻图片。失败静默忽略。</summary>
    public static async Task PreloadAsync(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            await LoadAsync(path, pin: false).ConfigureAwait(false);
        }
        catch
        {
            // 预加载失败不影响主流程
        }
    }

    /// <summary>固定当前显示的图片，使其不被缓存淘汰。</summary>
    public static void Pin(string path) => Cache.Pin(path);

    /// <summary>解除一次固定，并在超出预算时触发淘汰。</summary>
    public static void Unpin(string path) => Cache.Unpin(path);

    /// <summary>清空图片缓存并释放全部实例。</summary>
    public static void ClearCache() => Cache.Clear();

    /// <summary>
    /// 判断指定路径是否是受支持的图片文件。
    /// </summary>
    public static bool IsSupported(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext.ToLowerInvariant());
    }

    /// <summary>
    /// 获取指定文件所在目录下的全部图片文件（按名称排序）。
    /// </summary>
    public static IReadOnlyList<string> GetImageFiles(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateFiles(dir)
                .Where(IsSupported)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            // 无访问权限等情况下退化为空列表
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 从拖放数据中提取图片文件路径，没有则返回 null。
    /// </summary>
    public static string? ExtractImagePath(IDataObject? data)
    {
        if (data?.GetDataPresent(DataFormats.FileDrop) != true)
        {
            return null;
        }

        if (data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
        {
            return null;
        }

        // 优先选择第一个图片文件；拖入文件夹时取其中第一张图片
        foreach (string file in files)
        {
            if (File.Exists(file) && IsSupported(file))
            {
                return file;
            }

            if (Directory.Exists(file))
            {
                string? first = Directory.EnumerateFiles(file)
                    .Where(IsSupported)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (first is not null)
                {
                    return first;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 生成状态栏展示用的描述文本（尺寸、格式、文件大小）。
    /// </summary>
    public static string Describe(DecodedImage image)
    {
        string sizeText;
        try
        {
            long bytes = new FileInfo(image.FilePath).Length;
            sizeText = bytes switch
            {
                >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F2} MB",
                >= 1024 => $"{bytes / 1024.0:F1} KB",
                _ => $"{bytes} B"
            };
        }
        catch
        {
            sizeText = "未知大小";
        }

        return $"{image.Describe()}  |  {sizeText}";
    }

    // ---------------------------------------------------------------- GIF / GDI+

    internal static DecodedImage LoadGif(string path, byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        try
        {
            // 流必须在图片生命周期内保持可用，因此不释放
            Image image = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: false);
            return DecodedImage.FromGdiImage(path, "GIF", image);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static DecodedImage LoadWithGdi(string path, byte[] bytes, string formatName)
    {
        var stream = new MemoryStream(bytes);
        try
        {
            Image image = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: false);
            return DecodedImage.FromGdiImage(path, formatName, image);
        }
        catch (OutOfMemoryException ex)
        {
            stream.Dispose();
            throw new ImageDecodeException("文件不是受支持的图片格式，或图片数据已损坏。", ex);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------- Magick.NET

    internal static DecodedImage LoadWithMagick(string path, byte[] bytes, string extension)
    {
        if (SvgExtensions.Contains(extension))
        {
            return LoadSvg(path, bytes, extension);
        }

        // dcraw 需要真实文件路径，RAW 一律按路径解码
        bool usePath = RawExtensions.Contains(extension);

        if (AnimatedExtensions.Contains(extension))
        {
            return LoadAnimated(path, bytes, usePath);
        }

        using MagickImage image = ReadSingle(path, bytes, usePath, settings: null);
        image.AutoOrient();
        Bitmap bitmap = image.ToBitmap();
        return DecodedImage.FromFrames(path, image.Format.ToString(), new[] { bitmap }, new[] { 0 }, 1);
    }

    internal static DecodedImage LoadAnimated(string path, byte[] bytes, bool usePath)
    {
        using var collection = usePath
            ? new MagickImageCollection(path)
            : new MagickImageCollection(bytes);

        if (collection.Count == 0)
        {
            throw new ImageDecodeException("文件中没有可解码的图片帧。");
        }

        if (collection.Count == 1)
        {
            var only = collection[0];
            only.AutoOrient();
            return DecodedImage.FromFrames(
                path, only.Format.ToString(), new[] { only.ToBitmap() }, new[] { 0 }, 1);
        }

        // 差异帧需要合并成完整帧后才能直接显示
        collection.Coalesce();

        int sourceCount = collection.Count;
        int take = CalculateFrameLimit(collection[0].Width, collection[0].Height, sourceCount);

        var warnings = new List<string>();
        if (take < sourceCount)
        {
            warnings.Add($"动画共 {sourceCount} 帧，为控制内存占用仅解码前 {take} 帧");
        }

        var frames = new List<Bitmap>(take);
        var delays = new List<int>(take);
        try
        {
            for (int i = 0; i < take; i++)
            {
                var frame = collection[i];
                // AnimationDelay 单位是 1/100 秒
                delays.Add((int)frame.AnimationDelay * 10);
                frames.Add(frame.ToBitmap());
            }
        }
        catch
        {
            foreach (Bitmap created in frames)
            {
                created.Dispose();
            }

            throw;
        }

        string format = collection[0].Format.ToString();
        return DecodedImage.FromFrames(path, format, frames.ToArray(), delays.ToArray(), sourceCount, warnings);
    }

    internal static DecodedImage LoadSvg(string path, byte[] bytes, string extension)
    {
        // 先按逻辑尺寸（96dpi）渲染；若图很小，再抬到 SvgInitialRasterFloor 以上，避免一打开就模糊
        double scale = 1.0;
        Bitmap raster = RasterizeSvg(bytes, scale);

        if (raster.Width > 0 && raster.Width < SvgInitialRasterFloor)
        {
            double better = Math.Min((double)SvgInitialRasterFloor / raster.Width, MaxSvgScale);
            raster.Dispose();
            scale = better;
            raster = RasterizeSvg(bytes, scale);
        }

        string formatName = extension.Equals(".svgz", StringComparison.OrdinalIgnoreCase) ? "SVGZ" : "SVG";
        var warnings = new List<string>
        {
            "矢量图：放大时会自动按当前缩放重新栅格化，保持边缘清晰"
        };

        return DecodedImage.FromSvg(path, formatName, bytes, raster, scale, warnings);
    }

    /// <summary>
    /// 把 SVG 源数据按指定倍率栅格化，scale = 1 对应 96dpi 下的逻辑尺寸。
    /// librsvg 需要真实文件路径，因此先写入临时文件再渲染。
    /// </summary>
    /// <param name="svgBytes">SVG（或 SVGZ）文件内容。</param>
    /// <param name="scale">渲染倍率，会换算为 density = 96 × scale。</param>
    public static Bitmap RasterizeSvg(byte[] svgBytes, double scale)
    {
        double density = 96 * Math.Clamp(scale, 0.05, MaxSvgScale);
        string temp = Path.Combine(Path.GetTempPath(), $"svgview-{Guid.NewGuid():N}.svg");

        try
        {
            File.WriteAllBytes(temp, svgBytes);

            var settings = new MagickReadSettings
            {
                BackgroundColor = MagickColors.Transparent,
                Density = new Density(density)
            };

            using var image = new MagickImage(temp, settings);
            if (image.Width == 0 || image.Height == 0)
            {
                throw new ImageDecodeException("SVG 没有可识别的画布尺寸。");
            }

            return image.ToBitmap();
        }
        catch (MagickException ex)
        {
            throw new ImageDecodeException(DescribeMagickFailure("SVG", ex), ex);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // 临时文件删除失败不影响主流程
            }
        }
    }

    /// <summary>读取单张图片：优先从字节流解码，失败时回退到按文件路径解码。</summary>
    private static MagickImage ReadSingle(string path, byte[] bytes, bool usePath, MagickReadSettings? settings)
    {
        if (!usePath)
        {
            try
            {
                return settings is null ? new MagickImage(bytes) : new MagickImage(bytes, settings);
            }
            catch (MagickException)
            {
                // 某些格式的解码器依赖文件名/真实文件，回退到按路径读取
            }
        }

        return settings is null ? new MagickImage(path) : new MagickImage(path, settings);
    }

    /// <summary>根据单帧尺寸计算最多解码多少帧。</summary>
    private static int CalculateFrameLimit(uint width, uint height, int sourceCount)
    {
        long perFramePixels = Math.Max(1L, (long)width * height);
        long budget = Math.Max(1L, MaxAnimationPixels / perFramePixels);
        return (int)Math.Clamp(Math.Min(sourceCount, Math.Min(MaxAnimationFrames, budget)), 1, sourceCount);
    }

    /// <summary>
    /// 检测 Magick.NET 原生库是否可用，可用返回 null，否则返回原因。
    /// </summary>
    private static string? EnsureMagick()
    {
        if (_magickAvailable is not null)
        {
            return _magickAvailable.Value ? null : MagickUnavailableReason;
        }

        try
        {
            _ = MagickNET.Version;
            _magickAvailable = true;
            return null;
        }
        catch (Exception ex)
        {
            _magickAvailable = false;
            LastMagickError = $"{ex.GetType().Name}: {ex.Message}";
            return MagickUnavailableReason;
        }
    }

    private static string MagickUnavailableReason =>
        $"图片解码组件初始化失败（{LastMagickError}），可能需要安装 Visual C++ 运行库";

    private static string? LastMagickError { get; set; }

    internal static string DescribeMagickFailure(string formatName, MagickException ex) => ex switch
    {
        MagickMissingDelegateErrorException => $"当前解码组件不支持 {formatName} 格式（缺少对应的编解码器）。",
        MagickCorruptImageErrorException => $"{formatName} 文件已损坏，或不是有效的图片数据。",
        MagickBlobErrorException => $"无法读取 {formatName} 文件内容。",
        MagickCoderErrorException => $"找不到 {formatName} 格式的解码器。",
        MagickDelegateErrorException => $"{formatName} 格式所需的外部解码组件调用失败。",
        _ => $"解码 {formatName} 失败：{ex.Message}"
    };

    // ---------------------------------------------------------------- 转存 / 格式转换

    /// <summary>
    /// 可作为转存目标的格式候选（按推荐顺序）。运行时还会用 SupportsWriting 过滤，
    /// 缺少编码器的格式（如 HEIC）会被自动剔除。
    /// </summary>
    private static readonly WriteFormat[] WriteCandidates =
    {
        new(MagickFormat.Png, ".png", "PNG 图片"),
        new(MagickFormat.Jpeg, ".jpg", "JPEG 图片"),
        new(MagickFormat.WebP, ".webp", "WebP 图片"),
        new(MagickFormat.Bmp, ".bmp", "BMP 图片"),
        new(MagickFormat.Tiff, ".tif", "TIFF 图片"),
        new(MagickFormat.Gif, ".gif", "GIF 图片"),
        new(MagickFormat.Jxl, ".jxl", "JPEG XL 图片"),
        new(MagickFormat.Qoi, ".qoi", "QOI 图片"),
        new(MagickFormat.Tga, ".tga", "TGA 图片"),
        new(MagickFormat.Avif, ".avif", "AVIF 图片"),
        new(MagickFormat.Ico, ".ico", "ICO 图标"),
    };

    private static IReadOnlyList<WriteFormat>? _writableFormats;
    private static string? _saveDialogFilter;

    /// <summary>当前构建实际支持写入的目标格式（已按 SupportsWriting 过滤）。</summary>
    public static IReadOnlyList<WriteFormat> WritableFormats => _writableFormats ??= BuildWritableFormats();

    /// <summary>“转存为”对话框使用的过滤器字符串。</summary>
    public static string SaveDialogFilter => _saveDialogFilter ??= BuildSaveFilter();

    /// <summary>
    /// 把静态图转存为另一种格式。重新读取源文件（画质优先），应用 EXIF 方向与当前视图旋转后写出。
    /// </summary>
    /// <param name="source">当前已加载的图片。</param>
    /// <param name="targetPath">目标文件路径（扩展名决定格式）。</param>
    /// <param name="format">目标格式。</param>
    /// <param name="rotationDegrees">顺时针旋转角度（一般来自视图，0/90/180/270）。</param>
    /// <exception cref="ImageDecodeException">组件不可用、源文件缺失或写入失败。</exception>
    public static void SaveStatic(DecodedImage source, string targetPath, MagickFormat format, int rotationDegrees)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureMagickForSave();

        try
        {
            using MagickImage image = ReadForSave(source);
            image.AutoOrient();
            if (rotationDegrees != 0)
            {
                image.Rotate(rotationDegrees);
            }

            PrepareForFormat(image, format);
            image.Format = format;
            image.Write(targetPath);
        }
        catch (MagickException ex)
        {
            throw new ImageDecodeException($"转存失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 把动态图导出为一个文件夹里的序列帧（统一 PNG，无损且保留透明）。
    /// 重新读取源文件并 Coalesce 合并差异帧，每帧都能独立显示。
    /// </summary>
    /// <param name="source">当前已加载的动态图。</param>
    /// <param name="targetFolder">目标文件夹，不存在会创建。</param>
    /// <param name="rotationDegrees">顺时针旋转角度。</param>
    /// <param name="baseName">帧文件名前缀，输出形如 <c>{baseName}_0000.png</c>。</param>
    /// <returns>实际导出的帧数。</returns>
    public static int SaveAnimatedFrames(DecodedImage source, string targetFolder, int rotationDegrees, string baseName)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureMagickForSave();

        string path = source.FilePath;
        if (!File.Exists(path))
        {
            throw new ImageDecodeException($"源文件已不存在，无法导出：{path}");
        }

        Directory.CreateDirectory(targetFolder);

        int written = 0;
        try
        {
            using var collection = new MagickImageCollection(path);
            if (collection.Count == 0)
            {
                throw new ImageDecodeException("源文件中没有可导出的帧。");
            }

            collection.Coalesce();

            for (int i = 0; i < collection.Count; i++)
            {
                IMagickImage<byte> frame = collection[i]; // 归 collection 所有，不单独释放
                frame.AutoOrient();
                if (rotationDegrees != 0)
                {
                    frame.Rotate(rotationDegrees);
                }

                frame.Format = MagickFormat.Png;
                frame.Write(Path.Combine(targetFolder, $"{baseName}_{i:D4}.png"));
                written++;
            }
        }
        catch (MagickException ex)
        {
            throw new ImageDecodeException($"导出序列帧失败：{ex.Message}", ex);
        }

        return written;
    }

    /// <summary>按扩展名查找目标格式，找不到时回退到 PNG。</summary>
    public static WriteFormat FormatForExtension(string? extension)
    {
        if (!string.IsNullOrEmpty(extension))
        {
            foreach (WriteFormat format in WritableFormats)
            {
                if (string.Equals(format.Extension, extension, StringComparison.OrdinalIgnoreCase))
                {
                    return format;
                }
            }

            // 常见别名回退
            switch (extension.ToLowerInvariant())
            {
                case ".jpeg":
                    return new WriteFormat(MagickFormat.Jpeg, ".jpg", "JPEG 图片");
                case ".tiff":
                    return new WriteFormat(MagickFormat.Tiff, ".tif", "TIFF 图片");
            }
        }

        return new WriteFormat(MagickFormat.Png, ".png", "PNG 图片");
    }

    /// <summary>为转存重新读取源文件：SVG 按当前栅格倍率的密度重渲染，其余按原样解码。</summary>
    private static MagickImage ReadForSave(DecodedImage source)
    {
        string path = source.FilePath;
        if (!File.Exists(path))
        {
            throw new ImageDecodeException($"源文件已不存在，无法转存：{path}");
        }

        if (source.CanRerender)
        {
            double scale = Math.Clamp(source.RasterScale, 1.0, MaxSvgScale);
            var settings = new MagickReadSettings
            {
                BackgroundColor = MagickColors.Transparent,
                Density = new Density(96 * scale)
            };
            return new MagickImage(path, settings);
        }

        return new MagickImage(path);
    }

    /// <summary>针对目标格式做必要的像素处理（如 JPEG 铺白底去透明）。</summary>
    private static void PrepareForFormat(MagickImage image, MagickFormat format)
    {
        // JPEG 不支持透明通道，先铺白底再去 alpha，否则透明区会变黑
        if (format is MagickFormat.Jpeg or MagickFormat.Jpg)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
        }
    }

    private static void EnsureMagickForSave()
    {
        string? error = EnsureMagick();
        if (error is not null)
        {
            throw new ImageDecodeException("转存功能需要图片解码组件，但该组件不可用：" + error);
        }
    }

    private static IReadOnlyList<WriteFormat> BuildWritableFormats()
    {
        if (EnsureMagick() is not null)
        {
            return Array.Empty<WriteFormat>();
        }

        var writable = new HashSet<MagickFormat>();
        foreach (IMagickFormatInfo info in MagickNET.SupportedFormats)
        {
            if (info.SupportsWriting)
            {
                writable.Add(info.Format);
            }
        }

        var list = new List<WriteFormat>();
        foreach (WriteFormat candidate in WriteCandidates)
        {
            if (writable.Contains(candidate.Format))
            {
                list.Add(candidate);
            }
        }

        return list;
    }

    private static string BuildSaveFilter()
    {
        var parts = new List<string>();
        foreach (WriteFormat format in WritableFormats)
        {
            parts.Add($"{format.Label} (*{format.Extension})");
            parts.Add("*" + format.Extension);
        }

        return parts.Count == 0 ? "PNG 图片 (*.png)|*.png" : string.Join("|", parts);
    }

    private static string BuildDialogFilter()
    {
        string all = string.Join(";", SupportedExtensions.Select(e => "*" + e));

        string common = string.Join(";", new[]
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico"
        }.Select(e => "*" + e));

        string modern = string.Join(";", new[]
        {
            ".psd", ".tga", ".heic", ".heif", ".avif", ".svg", ".jxl", ".qoi"
        }.Select(e => "*" + e));

        string raw = string.Join(";", RawExtensions.OrderBy(e => e).Select(e => "*" + e));

        return $"常用图片|{common}|现代格式|{modern}|相机 RAW|{raw}|所有支持的图片|{all}|所有文件|*.*";
    }

    // ---------------------------------------------------------------- 元数据 / 缩略图

    /// <summary>
    /// 读取图片的元数据：文件级信息 + EXIF（相机型号、光圈、快门、ISO、拍摄时间等，若存在）。
    /// 解码时已丢弃 EXIF，因此需要重新读取源文件，属于较重的操作，适合后台调用。
    /// </summary>
    public static IReadOnlyList<MetadataItem> ReadMetadata(DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var items = new List<MetadataItem>
        {
            new("文件名", Path.GetFileName(image.FilePath)),
            new("所在目录", Path.GetDirectoryName(image.FilePath) ?? string.Empty),
            new("格式", image.FormatName),
            new("尺寸", $"{image.Width} × {image.Height}")
        };

        if (image.IsAnimated)
        {
            items.Add(new MetadataItem("动画帧数", image.FrameCount.ToString()));
        }

        try
        {
            var info = new FileInfo(image.FilePath);
            if (info.Exists)
            {
                items.Add(new MetadataItem("文件大小", FormatBytes(info.Length)));
                items.Add(new MetadataItem("修改时间", info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")));
            }
        }
        catch
        {
            // 文件信息读取失败不影响 EXIF 部分
        }

        if (EnsureMagick() is null)
        {
            items.AddRange(ReadExif(image.FilePath));
        }

        return items;
    }

    /// <summary>从源文件读取 EXIF 信息，只收集实际存在的字段。</summary>
    private static IEnumerable<MetadataItem> ReadExif(string path)
    {
        var result = new List<MetadataItem>();

        try
        {
            using var image = new MagickImage(path);
            IExifProfile? exif = image.GetExifProfile();
            if (exif is null)
            {
                return result;
            }

            AddText(result, "相机厂商", Str(exif, ExifTag.Make));
            AddText(result, "相机型号", Str(exif, ExifTag.Model));

            string? lensMake = Str(exif, ExifTag.LensMake);
            string? lensModel = Str(exif, ExifTag.LensModel);
            string lens = string.IsNullOrWhiteSpace(lensModel)
                ? lensMake ?? string.Empty
                : (string.IsNullOrWhiteSpace(lensMake) ? lensModel! : $"{lensMake} {lensModel}");
            AddText(result, "镜头", lens);

            AddText(result, "拍摄时间", FormatExifDate(Str(exif, ExifTag.DateTimeOriginal)));

            Rational? exposure = Rat(exif, ExifTag.ExposureTime);
            if (exposure is { } et)
            {
                AddText(result, "快门", FormatExposure(et));
            }

            Rational? fnumber = Rat(exif, ExifTag.FNumber);
            if (fnumber is { } fn && fn.ToDouble() > 0)
            {
                AddText(result, "光圈", $"f/{fn.ToDouble():0.0#}");
            }

            ushort[]? iso = UShortArray(exif, ExifTag.ISOSpeedRatings);
            if (iso is { Length: > 0 })
            {
                AddText(result, "ISO", iso[0].ToString());
            }

            Rational? focal = Rat(exif, ExifTag.FocalLength);
            if (focal is { } fl && fl.ToDouble() > 0)
            {
                AddText(result, "焦距", $"{fl.ToDouble():0.#} mm");
            }

            SignedRational? bias = SRat(exif, ExifTag.ExposureBiasValue);
            if (bias is { } eb && Math.Abs(eb.ToDouble()) > 1e-6)
            {
                AddText(result, "曝光补偿", $"{eb.ToDouble():+0.##;-0.##;0} EV");
            }

            ushort? flash = UShort(exif, ExifTag.Flash);
            if (flash is { } fl2)
            {
                AddText(result, "闪光灯", (fl2 & 0x1) == 1 ? "已闪光" : "未闪光");
            }

            ushort? wb = UShort(exif, ExifTag.WhiteBalance);
            if (wb is { } w)
            {
                AddText(result, "白平衡", w == 0 ? "自动" : "手动");
            }

            ushort? program = UShort(exif, ExifTag.ExposureProgram);
            if (program is { } p)
            {
                AddText(result, "曝光程序", FormatExposureProgram(p));
            }

            ushort? metering = UShort(exif, ExifTag.MeteringMode);
            if (metering is { } m)
            {
                AddText(result, "测光模式", FormatMeteringMode(m));
            }

            AddText(result, "软件", Str(exif, ExifTag.Software));
            AddText(result, "作者", Str(exif, ExifTag.Artist));
            AddText(result, "版权", Str(exif, ExifTag.Copyright));
        }
        catch (MagickException)
        {
            // EXIF 读取失败时，仅保留已有的文件级信息
        }

        return result;
    }

    private static string? Str(IExifProfile exif, ExifTag<string> tag)
    {
        try
        {
            return exif.GetValue(tag)?.Value;
        }
        catch (MagickException)
        {
            return null;
        }
    }

    private static Rational? Rat(IExifProfile exif, ExifTag<Rational> tag)
    {
        try
        {
            return exif.GetValue(tag)?.Value;
        }
        catch (MagickException)
        {
            return null;
        }
    }

    private static SignedRational? SRat(IExifProfile exif, ExifTag<SignedRational> tag)
    {
        try
        {
            return exif.GetValue(tag)?.Value;
        }
        catch (MagickException)
        {
            return null;
        }
    }

    private static ushort? UShort(IExifProfile exif, ExifTag<ushort> tag)
    {
        try
        {
            return exif.GetValue(tag)?.Value;
        }
        catch (MagickException)
        {
            return null;
        }
    }

    private static ushort[]? UShortArray(IExifProfile exif, ExifTag<ushort[]> tag)
    {
        try
        {
            return exif.GetValue(tag)?.Value;
        }
        catch (MagickException)
        {
            return null;
        }
    }

    private static void AddText(List<MetadataItem> list, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            list.Add(new MetadataItem(label, value.Trim()));
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F2} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };

    private static string? FormatExifDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        // EXIF 日期形如 "2023:05:01 12:34:56"，把日期部分的分隔符换成 "-"
        string s = raw.Trim();
        if (s.Length >= 10 && s[4] == ':' && s[7] == ':')
        {
            s = s.Substring(0, 10).Replace(':', '-') + s.Substring(10);
        }

        return s;
    }

    private static string FormatExposure(Rational exposure)
    {
        double seconds = exposure.ToDouble();
        if (seconds <= 0)
        {
            return string.Empty;
        }

        return seconds < 1 ? $"1/{Math.Round(1 / seconds)} 秒" : $"{seconds:0.##} 秒";
    }

    private static string FormatExposureProgram(ushort v) => v switch
    {
        0 => "未定义",
        1 => "手动",
        2 => "程序自动",
        3 => "光圈优先",
        4 => "快门优先",
        5 => "创意",
        6 => "动作",
        7 => "人像",
        8 => "风景",
        _ => $"程序 {v}"
    };

    private static string FormatMeteringMode(ushort v) => v switch
    {
        1 => "平均测光",
        2 => "中央重点",
        3 => "点测光",
        4 => "多点测光",
        5 => "评价测光",
        6 => "局部测光",
        _ => $"模式 {v}"
    };

    /// <summary>
    /// 生成缩略图（等比缩放到 <paramref name="maxSize"/> 以内），失败返回 null。
    /// 供导航侧边栏在后台线程按需调用。优先用 Magick.NET（支持全格式），不可用时回退 GDI+。
    /// </summary>
    public static Bitmap? LoadThumbnail(string path, int maxSize)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        uint size = (uint)Math.Max(8, maxSize);

        if (EnsureMagick() is null)
        {
            try
            {
                using var image = new MagickImage(path);
                image.AutoOrient();
                image.Thumbnail(size, size);
                image.Format = MagickFormat.Bgra;
                return image.ToBitmap();
            }
            catch (MagickException)
            {
                // 落到 GDI+ 兜底
            }
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            using Image img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
            return MakeThumbnailGdi(img, (int)size);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap MakeThumbnailGdi(Image source, int maxSize)
    {
        double scale = Math.Min((double)maxSize / source.Width, (double)maxSize / source.Height);
        if (scale <= 0 || double.IsInfinity(scale) || double.IsNaN(scale))
        {
            scale = 1;
        }

        int w = Math.Max(1, (int)Math.Round(source.Width * scale));
        int h = Math.Max(1, (int)Math.Round(source.Height * scale));

        var bmp = new Bitmap(w, h);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(source, 0, 0, w, h);
        }

        return bmp;
    }
}

/// <summary>
/// 一个可作为转存目标的图片格式。
/// </summary>
/// <param name="Format">Magick.NET 格式枚举。</param>
/// <param name="Extension">对应扩展名（含点，小写）。</param>
/// <param name="Label">对话框中显示的名称。</param>
public readonly record struct WriteFormat(MagickFormat Format, string Extension, string Label);

/// <summary>
/// 一条图片元数据（属性名 + 值），用于右侧信息面板展示。
/// </summary>
/// <param name="Label">属性名称。</param>
/// <param name="Value">属性值。</param>
public readonly record struct MetadataItem(string Label, string Value);

/// <summary>
/// 图片解码失败时抛出的异常。
/// </summary>
public sealed class ImageDecodeException : Exception
{
    public ImageDecodeException(string message)
        : base(message)
    {
    }

    public ImageDecodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
