# PROJECT_HANDOVER — ImageCache 引用计数架构

本文档记录图片查看器中 `ImageCache`（`ImageCache.cs`）的引用计数（Pin/Unpin）架构改动，
包含设计思路、Pin/Unpin 流程，以及“为什么用 `RefCount` 而不是 `bool`”。

---

## 1. 背景与要解决的问题

图片查看器引入了三项相互关联的能力：

1. **LRU 内存缓存**：来回切换图片时命中缓存复用已解码的 `DecodedImage`，避免重复解码。
2. **全异步加载**：解码在后台线程进行，UI 不卡死。
3. **相邻预加载**：浏览当前图时，后台提前解码上一张 / 下一张。

这三项叠加后，产生了一个致命的并发风险：

> **后台预加载线程提交新图 → 触发 LRU 淘汰 → 淘汰逻辑可能释放掉 UI 线程此刻正在绘制的位图 → 崩溃（`ObjectDisposedException` / GDI+ 报错）。**

因此必须有一种机制，能明确标记“这张图正在被 UI 使用，绝不允许淘汰”。这就是 `Pin/Unpin` 引用计数的由来。

---

## 2. 所有权契约（最重要的一条约定）

- **`DecodedImage` 实例的生命周期归 `ImageCache` 所有。**
- 缓存负责在淘汰 / 清空时调用 `DecodedImage.Dispose()`。
- **调用方（`MainForm`）绝不允许自行 `Dispose` 从缓存取得的图片**，只能通过 `Pin`/`Unpin` 表达“我还在用 / 我用完了”。

这条契约把“谁负责释放”这个最容易出错的并发问题收敛到单一所有者（缓存），
调用方只操作计数，不操作内存，从根本上杜绝了“双重释放”和“释放正在使用的对象”。

---

## 3. 为什么用 `RefCount`（int）而不是 `bool`

这是本次架构的核心决策。对比如下：

| 场景 | `bool IsPinned` | `int RefCount`（当前方案） |
| --- | --- | --- |
| 单张图被多处同时固定 | 无法区分，一方 Unpin 就把另一方仍在用的图解锁 | 每处各自 +1 / -1，只有全部释放后计数才归零 |
| 并发 Pin/Unpin 交错 | `true`/`false` 覆盖式赋值，后写覆盖先写，计数信息丢失 | 原子加减，精确配对，最终值 = 加次数 − 减次数 |
| 异步“过期结果”分支 | 无法知道该不该解锁（可能已被别处重新固定） | 过期分支照常 -1，不影响其它持有者的计数 |
| 精确归零可验证性 | 只能知道“有没有被固定”，不知道“被固定几次” | 可用 `GetRefCount` 断言精确归零，便于写并发单测 |

**一句话结论**：`bool` 只能表达“是/否被占用”，无法承载“被多少个持有者占用”；
在“当前显示 + 预加载 + 异步过期回滚”多方并发固定的场景下，只有引用计数才能做到**精确配对、精确归零、绝不误淘汰**。

---

## 4. 数据结构

`ImageCache` 内部：

- `Dictionary<string, Entry> _map`：路径 → 缓存项，O(1) 查找。
- `LinkedList<string> _lru`：LRU 顺序，`First` = 最近使用，`Last` = 最久未用。
- `long _usedBytes`：已用内存估算，配合 `BudgetBytes`（默认 768MB）与 `MaxEntries`（默认 8）触发淘汰。
- `object _sync`：所有结构性变更在此锁内串行化。

`Entry` 内部：

- `int _refCount`：**固定引用计数，`> 0` 表示正在被使用，永不被淘汰。**
- 计数的读写全部走 `Interlocked`（见第 7 节）。

---

## 5. Pin / Unpin 流程

### Pin（固定，引用计数 +1）
```
lock(_sync)
  找到 Entry → Interlocked.Increment(refCount) → Touch(移到 LRU 头部) → 记录日志
  找不到 → 记录 “Pin ignored(not cached)”
```

### Unpin（解除固定，引用计数 −1）
```
lock(_sync)
  找到 Entry → TryRelease()（CAS 自旋，仅当 >0 才减，返回是否真的减了）
             → Touch → 记录日志（含最新 refCount 与 released 标志）
  找不到 → 记录 “Unpin ignored(not cached)”
  Trim()   → 若超预算 / 超数量，按 LRU 淘汰 refCount==0 的项
```

### 淘汰（Trim）
```
while (超预算 || 超数量)
  从 LRU 尾部向前找第一个 refCount==0 的项作为 victim
  找不到（全部固定）→ 记录 “Trim blocked: all pinned” 并停止（固定项绝不淘汰）
  找到 → 从 map/lru 移除 → _usedBytes 减 → 记录 “Evict path=... refCount=0” → Dispose()
```

### 清空（Clear）
```
lock(_sync)
  逐项记录 “Clear dispose path=... refCount=...” 并 Dispose()
  清空 map/lru、_usedBytes 归零 → 记录 “Clear done count=N”
```

---

## 6. MainForm 中的配对释放（try/finally 保障）

`MainForm` 严格保证每一次 `Pin` 都有配对的 `Unpin`，即使 UI 绘制 / 清理过程抛异常：

- **`LoadImageAsync`**：以 `LoadAsync(path, pin:true)` 为新图加固定；随后整个 UI 接管流程包在 `try` 中：
  - 成功（`committed = true`）：在 `finally` 中解除**上一张**的固定（`Unpin(previousPath)`）。新图的固定继续由 `_current` 持有。
  - 失败：在 `finally` 中回退 `_current`/`_currentPath` 到上一张，并 `Unpin(path)` 释放本次为新图加的固定，避免 pin 泄漏。
  - 异步过期分支（`sequence != _loadSequence`）：直接 `Unpin(path)` 后返回。
- **`CloseImage`**：UI 清理包在 `try` 中，`Unpin(path)` 放在 `finally`，确保即便清理抛异常也解除固定。
- **`Dispose`**：调用 `ImageLoader.ClearCache()` 一次性释放全部缓存实例。

> 关键点：`MainForm` 只做计数的加减，从不调用 `DecodedImage.Dispose()`；释放权始终在缓存手里。

---

## 7. 原子性（Interlocked）与线程安全

`Entry` 的计数操作全部使用 `System.Threading.Interlocked`：

- `AddRef()` → `Interlocked.Increment(ref _refCount)`
- `TryRelease()` → `Interlocked.CompareExchange` 自旋，仅当 `> 0` 时递减（下限钳制到 0，永不为负）
- `ResetRefCount(v)` → `Interlocked.Exchange(ref _refCount, v)`（仅新建条目时在锁内初始化）
- 读取 `RefCount` → `Volatile.Read(ref _refCount)`

**为什么锁内还要用 Interlocked？**
结构性变更（增删 map/lru）已由 `_sync` 串行化，理论上锁内 `++/--` 已足够。仍统一改用 `Interlocked` 是为了：

1. 让 `GetRefCount(path)` 这类**诊断读取**拿到一致、不撕裂的计数值；
2. 让计数语义与锁解耦，未来若把热点路径改为无锁读取也无需重写计数逻辑；
3. 明确表达“这是并发计数字段”的意图，避免后续维护者误用非原子的 `++/--`。

---

## 8. 调试日志

`ImageCache.Logger`（`static Action<string>?`，默认写 `System.Diagnostics.Trace`）在
**Pin / Unpin / 淘汰 / 清空 / 提交 / 命中** 时输出，每条都带：

- 线程号 `[T<id>]`（便于观察并发交错）
- 图片路径 `path=...`
- 当前引用计数 `refCount=...`
- 淘汰 / 提交时附带 `bytes`、`used`、`count` 等预算信息

**用法示例**（重定向到自定义目标，如文件 / 列表）：
```csharp
ImageCache.Logger = msg => File.AppendAllText("cache.log", msg + Environment.NewLine);
```
设为 `null` 即完全关闭；由于用了 `?.` 短路，关闭时连日志字符串都不会拼接，压测路径零额外开销。

---

## 9. 单元测试（WinFormsApp1.Tests）

测试项目：`WinFormsApp1.Tests/`（xUnit，`net8.0-windows`，`ProjectReference` 主项目）。
主 `csproj` 已通过 `DefaultItemExcludes` 排除该子目录，避免测试代码被主项目通配编译。

覆盖点（`ImageCacheTests.cs`）：

- 基础：Pin/Unpin 增减、未缓存返回 `-1`、Unpin 不会变负。
- 淘汰保护：固定项在数量压力下 / 内存预算压力下（即使它是最久未用）**绝不被淘汰**；未固定项按预算被淘汰；Unpin 后恢复可淘汰。
- **并发压测**：
  - `ConcurrentPinStorm_...`：8 线程 × 5000 次 Pin，断言计数精确 = 40000（无丢失自增）。
  - `ConcurrentUnpinStorm_...`：先加满再多线程 Unpin，断言**精确归零**。
  - `ConcurrentMixedPinAndUnpin_...`：预置 baseline 后 Pin/Unpin 两侧并发对轰，断言净计数精确。
  - `PinnedItem_Survives_ConcurrentEvictionPressure`：2000 次并发提交+淘汰中，固定项始终存活且计数不变。
  - `ConcurrentPinUnpin_PairedPerThread_EndsAtZero`：16 线程各自成对 Pin/Unpin，最终归零。
- 日志：验证 Pin/Unpin/Evict/Clear 都记录了路径与 refCount。

运行：
```powershell
$dotnet = Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'
& $dotnet test WinFormsApp1.Tests\WinFormsApp1.Tests.csproj
```

---

## 10. 混合并发测试为何要“预置 baseline”

`ConcurrentMixedPinAndUnpin` 在并发对轰前先把计数加到 `= Unpin 总次数`。
原因：`TryRelease()` 有“不小于 0”的下限钳制。若从 0 开始，Unpin 可能跑在 Pin 之前被钳制成空操作，
最终值就变得不确定。预置 baseline 后，任意交错前缀下计数都 `≥ 0` 且不会触发钳制，
于是最终值恒等于 `baseline + pins − unpins = baseline`（两侧次数相等），断言确定、可复现。

---

## 11. 交接指南：如何「加图标」与「改名字」（无协助也能独立完成）

> 本项目当前**没有应用图标**，显示名固定为「图片查看器」，程序集/命名空间/exe 名为 `WinFormsApp1`。
> 下面按**风险从低到高**分五档，改到哪一档取决于你想改多深。改完统一跑 `build-installer.ps1` 重新打包。
> 注意：真正被使用的是 `MainForm.cs`（`Program.cs` 里 `new MainForm()`）；`Form1.cs` / `Form1.Designer.cs` 是 VS 模板遗留、**未被引用**，确认后可删除以免混淆。

### 档位 A：加图标（推荐先做，低风险）

1. 准备一个多尺寸图标 `app.ico`（建议含 16×16 / 32×32 / 48×48 / 256×256），放到项目根目录 `WinFormsApp1\app.ico`。
2. 让 **exe 带图标**（任务栏、资源管理器、安装包卸载项都会跟随它）——在 `WinFormsApp1.csproj` 的 `<PropertyGroup>` 里加一行：
   ```xml
   <ApplicationIcon>app.ico</ApplicationIcon>
   ```
3. 让**窗口左上角**也显示图标——在 `MainForm` 构造函数（`Text = "图片查看器";` 附近）加一行，直接从 exe 提取，无需额外复制文件：
   ```csharp
   Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
   ```
4. （可选）安装程序自身图标：在 `.iss` 的 `[Setup]` 段加 `SetupIconFile=..\app.ico`。
   `UninstallDisplayIcon={app}\{#MyAppExeName}` 已指向 exe，会自动跟随第 2 步的图标，无需再改。

### 档位 B：改「显示名字」（推荐，低风险，用户可见的名字都在这里）

只改字符串，不动程序集，最安全。建议先把标题抽成常量，将来只改一处：

1. 在 `MainForm.cs` 类顶部加常量：
   ```csharp
   private const string AppTitle = "新名字";
   ```
2. 把这三处硬编码的 `"图片查看器"` 换成 `AppTitle`：
   - `MainForm.cs` L44：`Text = "图片查看器";`（构造函数）
   - `MainForm.cs` L439：`Text = "图片查看器";`（`CloseImage` 关闭图片后重置标题）
   - `MainForm.cs` L757：`Text = $"图片查看器 - {Path.GetFileName(path)}";` → `Text = $"{AppTitle} - {Path.GetFileName(path)}";`
3. 改安装脚本 `installer\图片查看器.iss`：
   - `#define MyAppName "图片查看器"` → 新名字（影响安装目录、开始菜单、桌面快捷方式、控制面板「程序和功能」里显示的名字）。
   - `#define MyAppPublisher "WinFormsApp1"` → 你的发布者名。
   - `[Code]` 段里写死的中文也要一并改，否则「打开方式 / 默认应用」列表里仍是旧名：
     L162 `'图片查看器可打开的图片'`、L167 `FriendlyAppName` 值 `'图片查看器'`、
     L171 `ApplicationName` 值 `'图片查看器'`、L172 `ApplicationDescription` 值 `'多格式图片查看器'`。
   - （可选）文件名 `图片查看器.iss` 本身也可改；`build-installer.ps1` 用通配符 `*.iss` 定位，改名不影响构建。

### 档位 C：改 exe / 程序集名（中风险，有连带项）

想把 `WinFormsApp1.exe` 变成 `新名字.exe`：

1. `WinFormsApp1.csproj` 的 `<PropertyGroup>` 加：
   ```xml
   <AssemblyName>新名字</AssemblyName>
   ```
   （生成的 exe 与主 dll 名随之改变。）
2. **必须连带改** `.iss` 的 `#define MyAppExeName "WinFormsApp1.exe"` → `"新名字.exe"`，否则 `[Icons]`/`[Run]`/`UninstallDisplayIcon` 会找不到文件、编译或运行出错。
3. 建议同步把 `[Code]` 段的标识改成新值：`ProgId = 'WinFormsApp1.Image'`、`AppRegKey = 'Software\WinFormsApp1'`。
   ⚠️ 迁移注意：ProgId / AppRegKey 改名后，**老版本已写入的旧关联不会被新版卸载逻辑清理**（清理按新 ProgId 比对）。若已有用户在用旧版，需另写一次性迁移或让用户先卸载旧版。

### 档位 D：改命名空间 `WinFormsApp1` → 新名（高风险，非必要不做）

这是**代码内部名字，用户看不到**，牵一发动全身，除非有洁癖否则**建议不改**：

1. 所有 `.cs` 顶部的 `namespace WinFormsApp1;` 要全改（用 Rider「重命名符号 / Rename」重构最安全，会自动同步引用）。
2. 连带需要改的外部引用：项目文件名 `WinFormsApp1.csproj`、解决方案 `WinFormsApp1.sln`、
   测试项目 `WinFormsApp1.Tests`（含其 `.csproj` 与 `ProjectReference`）、
   主 csproj 里的 `DefaultItemExcludes`（`WinFormsApp1.Tests\**`）、
   `build-installer.ps1` 里写死的 `WinFormsApp1.csproj`。

### 档位 E：改完如何重新打包

```powershell
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
```
该脚本会：自包含 + ReadyToRun 发布到 `bin\Publish\win-x64` → 定位 ISCC.exe → 编译 `installer\*.iss` → 在 `bin\Publish\Installer\` 生成 setup.exe。

> 版本号在 `.iss` 的 `#define MyAppVersion "1.0.0"`。不改版本号时，新 setup.exe 会**覆盖同名旧包**；
> 想保留历史包就升版本号，或把 `OutputBaseFilename` 加上时间戳。
