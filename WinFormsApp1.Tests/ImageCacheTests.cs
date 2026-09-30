using System.Drawing;
using WinFormsApp1;
using Xunit;

namespace WinFormsApp1.Tests;

/// <summary>
/// <see cref="ImageCache"/> 引用计数机制的单元测试，重点覆盖多线程并发场景：
/// 一边疯狂 Pin、一边疯狂 Unpin，验证 RefCount 能否精确归零、固定项是否绝不会被淘汰。
///
/// 说明：淘汰 / 存在性通过 <see cref="ImageCache.TryGet"/> 观测（被淘汰即返回 false）；
/// RefCount 通过 <see cref="ImageCache.GetRefCount"/> 原子读取观测。
/// </summary>
public sealed class ImageCacheTests
{
    public ImageCacheTests()
    {
        // 默认关闭日志：避免压测里 Trace 输出拖慢速度，也避免并行写 List 冲突。
        ImageCache.Logger = null;
    }

    // ------------------------------------------------------------ 辅助

    private static DecodedImage NewImage(string path, int width = 4, int height = 4)
    {
        var frame = new Bitmap(width, height);
        return DecodedImage.FromFrames(path, "PNG", new[] { frame }, new[] { 0 }, 1);
    }

    private static ImageCache NewCache(int maxEntries = 8, long budgetBytes = long.MaxValue) =>
        new() { MaxEntries = maxEntries, BudgetBytes = budgetBytes };

    /// <summary>每张 4×4×4 = 64 字节，用于按内存预算触发淘汰。</summary>
    private const int BytesPerImage = 4 * 4 * 4;

    // ------------------------------------------------------------ 基础引用计数

    [Fact]
    public void Pin_IncrementsRefCount_Unpin_DecrementsIt()
    {
        ImageCache cache = NewCache();
        const string p = "a.png";

        cache.Commit(p, NewImage(p), pin: true);
        Assert.Equal(1, cache.GetRefCount(p));

        cache.Pin(p);
        Assert.Equal(2, cache.GetRefCount(p));

        cache.Unpin(p);
        Assert.Equal(1, cache.GetRefCount(p));

        cache.Unpin(p);
        Assert.Equal(0, cache.GetRefCount(p));
    }

    [Fact]
    public void GetRefCount_ReturnsMinusOne_WhenNotCached()
    {
        ImageCache cache = NewCache();
        Assert.Equal(-1, cache.GetRefCount("missing.png"));
    }

    [Fact]
    public void Unpin_NeverDrivesRefCountNegative()
    {
        ImageCache cache = NewCache();
        const string p = "a.png";

        cache.Commit(p, NewImage(p), pin: false); // refCount 0
        cache.Unpin(p);
        cache.Unpin(p);
        cache.Unpin(p);

        Assert.Equal(0, cache.GetRefCount(p));
    }

    // ------------------------------------------------------------ 淘汰与固定保护

    [Fact]
    public void PinnedItem_IsNeverEvicted_UnderCountPressure()
    {
        ImageCache cache = NewCache(maxEntries: 3);
        const string pinned = "keep.png";
        cache.Commit(pinned, NewImage(pinned), pin: true);

        // 灌入远超上限的未固定项，反复触发 Trim
        for (int i = 0; i < 50; i++)
        {
            string p = $"f{i}.png";
            cache.Commit(p, NewImage(p), pin: false);
        }

        Assert.True(cache.TryGet(pinned, out DecodedImage? img));
        Assert.NotNull(img);
        Assert.Equal(1, cache.GetRefCount(pinned));
    }

    [Fact]
    public void UnpinnedItem_IsEvicted_WhenOverBudget()
    {
        ImageCache cache = new() { MaxEntries = 100, BudgetBytes = BytesPerImage * 3 }; // 只容得下 3 张
        const string first = "first.png";
        cache.Commit(first, NewImage(first), pin: false);

        for (int i = 0; i < 10; i++)
        {
            string p = $"x{i}.png";
            cache.Commit(p, NewImage(p), pin: false);
        }

        Assert.False(cache.TryGet(first, out _)); // 最久未用者已被淘汰
    }

    [Fact]
    public void PinnedItem_IsNotEvicted_EvenWhenItIsLeastRecentlyUsed()
    {
        // 预算只够 2 张；固定项即使变成最久未用，也必须存活，未固定的替它被淘汰。
        ImageCache cache = new() { MaxEntries = 100, BudgetBytes = BytesPerImage * 2 };
        const string pinned = "pinned.png";
        cache.Commit(pinned, NewImage(pinned), pin: true);

        for (int i = 0; i < 10; i++)
        {
            string p = $"filler{i}.png";
            cache.Commit(p, NewImage(p), pin: false);
        }

        Assert.True(cache.TryGet(pinned, out _));
        Assert.Equal(1, cache.GetRefCount(pinned));
    }

    [Fact]
    public void Unpin_AllowsEviction_Afterwards()
    {
        ImageCache cache = new() { MaxEntries = 2, BudgetBytes = long.MaxValue };
        const string target = "target.png";
        cache.Commit(target, NewImage(target), pin: true);
        cache.Unpin(target); // 解除固定后应可被淘汰

        cache.Commit("b.png", NewImage("b.png"), pin: false);
        cache.Commit("c.png", NewImage("c.png"), pin: false);

        Assert.False(cache.TryGet(target, out _));
    }

    // ------------------------------------------------------------ 多线程并发压测

    [Fact]
    public void ConcurrentPinStorm_RefCountIsExact_NoLostIncrements()
    {
        ImageCache cache = NewCache();
        const string p = "storm.png";
        cache.Commit(p, NewImage(p), pin: false);

        const int threads = 8;
        const int perThread = 5000;

        Parallel.For(0, threads, _ =>
        {
            for (int i = 0; i < perThread; i++)
            {
                cache.Pin(p);
            }
        });

        Assert.Equal(threads * perThread, cache.GetRefCount(p));
    }

    [Fact]
    public void ConcurrentUnpinStorm_RefCountReturnsPreciselyToZero()
    {
        ImageCache cache = NewCache();
        const string p = "storm.png";
        cache.Commit(p, NewImage(p), pin: false);

        const int threads = 8;
        const int perThread = 5000;
        const int total = threads * perThread;

        for (int i = 0; i < total; i++)
        {
            cache.Pin(p);
        }

        Assert.Equal(total, cache.GetRefCount(p));

        Parallel.For(0, threads, _ =>
        {
            for (int i = 0; i < perThread; i++)
            {
                cache.Unpin(p);
            }
        });

        Assert.Equal(0, cache.GetRefCount(p)); // 精确归零
    }

    [Fact]
    public void ConcurrentMixedPinAndUnpin_NetRefCountIsExact()
    {
        ImageCache cache = NewCache();
        const string p = "mixed.png";
        cache.Commit(p, NewImage(p), pin: false);

        const int threads = 4;
        const int perThread = 5000;
        const int totalPerSide = threads * perThread;

        // 预置 baseline = 全部 Unpin 次数：保证并发任意交错时 Unpin 都不会触发“下限钳制”，
        // 于是最终值必然等于 baseline + pins - unpins = baseline（两侧次数相等）。
        for (int i = 0; i < totalPerSide; i++)
        {
            cache.Pin(p);
        }

        int baseline = cache.GetRefCount(p);
        Assert.Equal(totalPerSide, baseline);

        int pinsDone = 0;
        int unpinsDone = 0;

        Parallel.Invoke(
            () => Parallel.For(0, threads, _ =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    cache.Pin(p);
                    Interlocked.Increment(ref pinsDone);
                }
            }),
            () => Parallel.For(0, threads, _ =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    cache.Unpin(p);
                    Interlocked.Increment(ref unpinsDone);
                }
            }));

        Assert.Equal(totalPerSide, pinsDone);
        Assert.Equal(totalPerSide, unpinsDone);
        Assert.Equal(baseline, cache.GetRefCount(p)); // 净计数精确
    }

    [Fact]
    public void PinnedItem_Survives_ConcurrentEvictionPressure()
    {
        // 多线程一边提交未固定项触发淘汰，一边验证固定项始终存活且计数不变。
        ImageCache cache = new() { MaxEntries = 4, BudgetBytes = 4096 };
        const string pinned = "pinned.png";
        cache.Commit(pinned, NewImage(pinned), pin: true);

        Parallel.For(0, 2000, i =>
        {
            string p = $"tmp{i}.png";
            cache.Commit(p, NewImage(p), pin: false);
            cache.Unpin(p); // 触发一次 Trim
        });

        Assert.True(cache.TryGet(pinned, out _));
        Assert.Equal(1, cache.GetRefCount(pinned));
    }

    [Fact]
    public void ConcurrentPinUnpin_PairedPerThread_EndsAtZero()
    {
        // 每个线程内部 Pin/Unpin 成对；多线程并发下最终仍应精确归零。
        ImageCache cache = NewCache();
        const string p = "paired.png";
        cache.Commit(p, NewImage(p), pin: false);

        const int threads = 16;
        const int perThread = 2000;

        Parallel.For(0, threads, _ =>
        {
            for (int i = 0; i < perThread; i++)
            {
                cache.Pin(p);
                cache.Unpin(p);
            }
        });

        Assert.Equal(0, cache.GetRefCount(p));
    }

    // ------------------------------------------------------------ 日志

    [Fact]
    public void Pin_Unpin_Evict_Clear_AreLogged_WithPathAndRefCount()
    {
        var logs = new List<string>();
        Action<string>? previous = ImageCache.Logger;
        ImageCache.Logger = logs.Add; // 本测试单线程，List 非线程安全无妨
        try
        {
            ImageCache cache = new() { MaxEntries = 1, BudgetBytes = long.MaxValue };
            const string a = "a.png";
            const string b = "b.png";

            cache.Commit(a, NewImage(a), pin: false);
            cache.Pin(a);      // refCount 0 -> 1
            cache.Unpin(a);    // refCount 1 -> 0
            cache.Commit(b, NewImage(b), pin: false); // MaxEntries=1，淘汰 a
            cache.Clear();
        }
        finally
        {
            ImageCache.Logger = previous;
        }

        Assert.Contains(logs, l => l.Contains("Pin path=a.png") && l.Contains("refCount=1"));
        Assert.Contains(logs, l => l.Contains("Unpin path=a.png") && l.Contains("refCount=0"));
        Assert.Contains(logs, l => l.Contains("Evict path=a.png"));
        Assert.Contains(logs, l => l.Contains("Clear done"));
    }
}
