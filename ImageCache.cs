using System.Diagnostics;

namespace WinFormsApp1;

/// <summary>
/// <see cref="DecodedImage"/> 的 LRU 内存缓存：来回切换图片时命中缓存即可复用，避免重复解码。
///
/// 生命周期约定：缓存“拥有”其中的实例，命中项由缓存负责 <see cref="DecodedImage.Dispose"/>，
/// 调用方（<see cref="MainForm"/>）不得再自行释放从缓存取得的图片。
///
/// 正在显示的当前图通过 <see cref="Pin"/> 固定（引用计数），固定项永不被淘汰，
/// 从根本上避免“后台预加载触发淘汰”把 UI 线程正在绘制的位图释放掉而崩溃。
///
/// 引用计数的加减一律走 <see cref="Interlocked"/> 原子操作，使得即使在锁外读取
/// <see cref="GetRefCount"/> 也能拿到一致的值；缓存结构性变更仍在 <c>_sync</c> 锁内串行化。
/// 所有公共方法均线程安全。可通过 <see cref="Logger"/> 打开并发调试日志。
/// </summary>
public sealed class ImageCache
{
    private sealed class Entry
    {
        private int _refCount;

        public Entry(string key, DecodedImage image)
        {
            Key = key;
            Image = image;
            Bytes = image.EstimatedBytes;
        }

        public string Key { get; }
        public DecodedImage Image { get; }
        public long Bytes { get; }

        public LinkedListNode<string>? Node { get; set; }

        /// <summary>固定引用计数，&gt; 0 表示正在被使用（当前显示），不可淘汰。原子读取。</summary>
        public int RefCount => Volatile.Read(ref _refCount);

        /// <summary>原子 +1，返回自增后的值。</summary>
        public int AddRef() => Interlocked.Increment(ref _refCount);

        /// <summary>
        /// 原子 -1，且保证不小于 0：用 <see cref="Interlocked.CompareExchange"/> 自旋，
        /// 仅当当前值 &gt; 0 时才递减。返回是否真的递减了（false 表示已是 0，被钳制）。
        /// </summary>
        public bool TryRelease()
        {
            while (true)
            {
                int current = Volatile.Read(ref _refCount);
                if (current <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _refCount, current - 1, current) == current)
                {
                    return true;
                }
            }
        }

        /// <summary>原子地设为指定值（仅在锁内新建条目时初始化使用）。</summary>
        public void ResetRefCount(int value) => Interlocked.Exchange(ref _refCount, value);
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new(); // First = 最近使用，Last = 最久未用
    private long _usedBytes;

    /// <summary>
    /// 缓存调试日志输出目标；设为 <c>null</c> 关闭日志。默认写入 <see cref="Trace"/>（调试输出窗口可见）。
    /// 每条日志都会带上路径与当前 RefCount，方便排查并发下的固定 / 解除固定 / 淘汰问题。
    /// </summary>
    public static Action<string>? Logger { get; set; } = static message => Trace.WriteLine(message);

    /// <summary>内存预算（字节），超出后按 LRU 淘汰未固定项。默认 768MB。</summary>
    public long BudgetBytes { get; set; } = 768L * 1024 * 1024;

    /// <summary>最多缓存的图片数量，超出后按 LRU 淘汰未固定项。</summary>
    public int MaxEntries { get; set; } = 8;

    /// <summary>当前缓存的图片数量。</summary>
    public int Count
    {
        get { lock (_sync) { return _map.Count; } }
    }

    /// <summary>诊断用：返回指定路径当前的固定引用计数；未缓存返回 -1。原子读取，可安全在任意线程调用。</summary>
    public int GetRefCount(string path)
    {
        lock (_sync)
        {
            return _map.TryGetValue(path, out Entry? entry) ? entry.RefCount : -1;
        }
    }

    // Logger 为 null 时短路，连字符串插值都不会执行，压测路径零额外开销。
    private static void Log(string message) =>
        Logger?.Invoke($"[ImageCache][T{Environment.CurrentManagedThreadId}] {message}");

    /// <summary>尝试取得缓存项并置为最近使用，命中返回 true。</summary>
    public bool TryGet(string path, out DecodedImage? image)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(path, out Entry? entry))
            {
                Touch(entry);
                image = entry.Image;
                Log($"Get hit path={path} refCount={entry.RefCount}");
                return true;
            }

            image = null;
            Log($"Get miss path={path}");
            return false;
        }
    }

    /// <summary>
    /// 把解码结果提交进缓存：不存在则新增，已存在则复用原实例（丢弃重复解码的 <paramref name="image"/>）。
    /// <paramref name="pin"/> 为 true 时增加固定引用计数；对已存在项只会“加固定”，不会解除既有固定。
    /// </summary>
    public void Commit(string path, DecodedImage image, bool pin)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(path, out Entry? existing))
            {
                if (!ReferenceEquals(existing.Image, image))
                {
                    image.Dispose(); // 并发重复解码，丢弃多出来的实例
                    Log($"Commit discard-duplicate path={path} refCount={existing.RefCount}");
                }

                int rc = pin ? existing.AddRef() : existing.RefCount;
                Touch(existing);
                Log($"Commit reuse path={path} pin={pin} refCount={rc} used={_usedBytes}/{BudgetBytes} count={_map.Count}");
                return;
            }

            var entry = new Entry(path, image);
            if (pin)
            {
                entry.ResetRefCount(1);
            }

            entry.Node = _lru.AddFirst(path);
            _map[path] = entry;
            _usedBytes += entry.Bytes;
            Log($"Commit add path={path} pin={pin} refCount={entry.RefCount} bytes={entry.Bytes} used={_usedBytes}/{BudgetBytes} count={_map.Count}");
            Trim();
        }
    }

    /// <summary>固定指定路径（引用计数 +1），使其不被淘汰。</summary>
    public void Pin(string path)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(path, out Entry? entry))
            {
                int rc = entry.AddRef();
                Touch(entry);
                Log($"Pin path={path} refCount={rc}");
            }
            else
            {
                Log($"Pin ignored(not cached) path={path}");
            }
        }
    }

    /// <summary>解除一次固定（引用计数 -1），并在超出预算时触发淘汰。</summary>
    public void Unpin(string path)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(path, out Entry? entry))
            {
                bool released = entry.TryRelease();
                Touch(entry);
                Log($"Unpin path={path} refCount={entry.RefCount} released={released}");
            }
            else
            {
                Log($"Unpin ignored(not cached) path={path}");
            }

            Trim();
        }
    }

    /// <summary>清空缓存并释放全部实例（窗口关闭时调用）。</summary>
    public void Clear()
    {
        lock (_sync)
        {
            foreach (Entry entry in _map.Values)
            {
                Log($"Clear dispose path={entry.Key} refCount={entry.RefCount} bytes={entry.Bytes}");
                entry.Image.Dispose();
            }

            int n = _map.Count;
            _map.Clear();
            _lru.Clear();
            _usedBytes = 0;
            Log($"Clear done count={n}");
        }
    }

    /// <summary>把条目移动到 LRU 头部（最近使用），调用前须持有 <see cref="_sync"/>。</summary>
    private void Touch(Entry entry)
    {
        if (entry.Node is { } node)
        {
            _lru.Remove(node);
        }

        entry.Node = _lru.AddFirst(entry.Key);
    }

    /// <summary>按 LRU 淘汰未固定项，直到满足内存预算与数量上限。调用前须持有 <see cref="_sync"/>。</summary>
    private void Trim()
    {
        while (_usedBytes > BudgetBytes || _map.Count > MaxEntries)
        {
            string? victim = null;
            for (LinkedListNode<string>? n = _lru.Last; n is not null; n = n.Previous)
            {
                if (_map.TryGetValue(n.Value, out Entry? candidate) && candidate.RefCount == 0)
                {
                    victim = n.Value;
                    break;
                }
            }

            if (victim is null)
            {
                Log($"Trim blocked: all pinned used={_usedBytes}/{BudgetBytes} count={_map.Count}/{MaxEntries}");
                break; // 全部处于固定状态，无法继续淘汰
            }

            Entry evict = _map[victim];
            if (evict.Node is { } node)
            {
                _lru.Remove(node);
            }

            _map.Remove(victim);
            _usedBytes -= evict.Bytes;
            Log($"Evict path={victim} refCount={evict.RefCount} bytes={evict.Bytes} used(after)={_usedBytes} count(after)={_map.Count}");
            evict.Image.Dispose();
        }
    }
}
