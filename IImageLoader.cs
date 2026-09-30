using ImageMagick;

namespace WinFormsApp1;

/// <summary>
/// 图片加载器的统一接口（策略模式）。每种/每类格式由一个具体加载器负责解码，
/// 由 <see cref="ImageLoaderFactory"/> 依据扩展名与运行时组件可用性挑选合适的实现。
/// </summary>
public interface IImageLoader
{
    /// <summary>加载器名称，用于诊断。</summary>
    string Name { get; }

    /// <summary>匹配优先级，数字越小越先被工厂选中。</summary>
    int Priority { get; }

    /// <summary>是否能处理指定扩展名（小写含点，如 ".png"）。</summary>
    bool CanLoad(string extension);

    /// <summary>解码图片，失败抛 <see cref="ImageDecodeException"/>。</summary>
    DecodedImage Load(ImageLoadRequest request);
}

/// <summary>
/// 一次加载请求的上下文：封装路径、扩展名，并按需惰性读取文件字节。
/// </summary>
public sealed class ImageLoadRequest
{
    private byte[]? _bytes;

    public ImageLoadRequest(string path)
    {
        Path = path;
        Extension = System.IO.Path.GetExtension(path);
    }

    /// <summary>图片文件的完整路径。</summary>
    public string Path { get; }

    /// <summary>文件扩展名（小写含点）。</summary>
    public string Extension { get; }

    /// <summary>大写、不含点的格式名，如 "PNG"，用于状态栏展示。</summary>
    public string FormatName => Extension.TrimStart('.').ToUpperInvariant();

    /// <summary>文件内容字节，首次访问时读取并缓存。</summary>
    public byte[] Bytes
    {
        get
        {
            if (_bytes is null)
            {
                try
                {
                    _bytes = File.ReadAllBytes(Path);
                }
                catch (Exception ex)
                {
                    throw new ImageDecodeException($"读取文件失败：{ex.Message}", ex);
                }
            }

            return _bytes;
        }
    }
}

/// <summary>GIF 加载器：交给 GDI+，可用 ImageAnimator 流式播放，内存占用与帧数无关。</summary>
public sealed class GifImageLoader : IImageLoader
{
    public string Name => "GIF (GDI+)";
    public int Priority => 10;
    public bool CanLoad(string extension) => extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
    public DecodedImage Load(ImageLoadRequest request) => ImageLoader.LoadGif(request.Path, request.Bytes);
}

/// <summary>图元文件（WMF/EMF）加载器：只能由 GDI+ 解码。</summary>
public sealed class MetafileImageLoader : IImageLoader
{
    public string Name => "WMF/EMF (GDI+)";
    public int Priority => 20;
    public bool CanLoad(string extension) => ImageLoader.GdiOnlyExtensions.Contains(extension);
    public DecodedImage Load(ImageLoadRequest request) =>
        ImageLoader.LoadWithGdi(request.Path, request.Bytes, request.FormatName);
}

/// <summary>SVG/SVGZ 矢量图加载器：由 Magick.NET（librsvg）栅格化，支持按需重渲染。</summary>
public sealed class SvgImageLoader : IImageLoader
{
    public string Name => "SVG (Magick.NET)";
    public int Priority => 30;
    public bool CanLoad(string extension) =>
        ImageLoader.IsMagickAvailable && ImageLoader.SvgExtensions.Contains(extension);
    public DecodedImage Load(ImageLoadRequest request) =>
        ImageLoader.LoadSvg(request.Path, request.Bytes, request.Extension);
}

/// <summary>相机 RAW 加载器：由 dcraw 解码，需要真实文件路径。</summary>
public sealed class RawImageLoader : IImageLoader
{
    public string Name => "RAW (Magick.NET)";
    public int Priority => 40;
    public bool CanLoad(string extension) =>
        ImageLoader.IsMagickAvailable && ImageLoader.RawExtensions.Contains(extension);
    public DecodedImage Load(ImageLoadRequest request) =>
        ImageLoader.LoadWithMagick(request.Path, request.Bytes, request.Extension);
}

/// <summary>多帧动画（WebP/JXL）加载器：由 Magick.NET 合并差异帧为帧序列。</summary>
public sealed class AnimatedImageLoader : IImageLoader
{
    public string Name => "动画 (Magick.NET)";
    public int Priority => 50;
    public bool CanLoad(string extension) =>
        ImageLoader.IsMagickAvailable && ImageLoader.AnimatedExtensions.Contains(extension);
    public DecodedImage Load(ImageLoadRequest request) =>
        ImageLoader.LoadAnimated(request.Path, request.Bytes, usePath: false);
}

/// <summary>
/// 通用静态图加载器：Magick.NET 可用时处理其余全部格式（PSD/TGA/HEIC/AVIF/JXL 静态等），
/// 解码失败且 GDI+ 能处理该格式时回退到 GDI+。
/// </summary>
public sealed class MagickBitmapImageLoader : IImageLoader
{
    public string Name => "静态图 (Magick.NET)";
    public int Priority => 60;
    public bool CanLoad(string extension) => ImageLoader.IsMagickAvailable;

    public DecodedImage Load(ImageLoadRequest request)
    {
        try
        {
            return ImageLoader.LoadWithMagick(request.Path, request.Bytes, request.Extension);
        }
        catch (MagickException ex)
        {
            // Magick.NET 解码失败时，GDI+ 还能救回来的格式就再试一次
            if (ImageLoader.GdiCapableExtensions.Contains(request.Extension))
            {
                try
                {
                    return ImageLoader.LoadWithGdi(request.Path, request.Bytes, request.FormatName);
                }
                catch
                {
                    // 忽略降级失败，抛出原始错误
                }
            }

            throw new ImageDecodeException(
                ImageLoader.DescribeMagickFailure(request.FormatName, ex), ex);
        }
    }
}

/// <summary>降级加载器：Magick.NET 不可用时，用 GDI+ 处理其能解码的常见格式。</summary>
public sealed class GdiFallbackImageLoader : IImageLoader
{
    public string Name => "降级 (GDI+)";
    public int Priority => 70;
    public bool CanLoad(string extension) =>
        !ImageLoader.IsMagickAvailable && ImageLoader.GdiCapableExtensions.Contains(extension);
    public DecodedImage Load(ImageLoadRequest request) =>
        ImageLoader.LoadWithGdi(request.Path, request.Bytes, request.FormatName);
}

/// <summary>
/// 图片加载器工厂：按优先级顺序持有全部 <see cref="IImageLoader"/>，
/// 依据扩展名与运行时组件可用性返回第一个可用的加载器（统一入口）。
/// </summary>
public static class ImageLoaderFactory
{
    private static readonly IImageLoader[] Loaders = new IImageLoader[]
    {
        new GifImageLoader(),
        new MetafileImageLoader(),
        new SvgImageLoader(),
        new RawImageLoader(),
        new AnimatedImageLoader(),
        new MagickBitmapImageLoader(),
        new GdiFallbackImageLoader()
    }.OrderBy(l => l.Priority).ToArray();

    /// <summary>全部已注册加载器（按优先级升序）。</summary>
    public static IReadOnlyList<IImageLoader> All => Loaders;

    /// <summary>返回能处理该扩展名的加载器，无匹配时返回 null。</summary>
    public static IImageLoader? Resolve(string extension)
    {
        foreach (IImageLoader loader in Loaders)
        {
            if (loader.CanLoad(extension))
            {
                return loader;
            }
        }

        return null;
    }
}
