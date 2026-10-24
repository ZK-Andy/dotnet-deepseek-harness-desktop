// PayloadSmoke — 打包期载荷冒烟探针（ADR testing/2026-09-13-payload-smoke-probe）。
// 用法（父进程，随 package 工作流在 publish 后调用）:
//   DeepSeek.Harness.Desktop.PayloadSmoke <publish 目录>
// 对自包含 publish 产物的 native 载荷面冒烟：清单存在性 → FFI 加载+导出解析 →
// 功能用例（经 Skia 的 sk_data_new_from_file / sk_image_new_from_encoded 真解码 icon.png）。
// 断言清单是宿主实际加载的面：Avalonia 渲染栈（Skia/HarfBuzz，macOS 另 libAvaloniaNative，
// Windows 另 ANGLE 的 av_libglesv2）。Ryn 系 native 库已随 G5 摘除出包引用，无断言面；Windows 的 WebView2 由控件的
// ManagedWebView2Loader 定位已装运行时——故均不在此断言。
// 功能用例在**子进程**中执行：native 侧硬崩溃/挂死被记为失败项并继续其余用例，
// 诊断清单（逐条列因，exit 1）不被单点崩溃截断。
// 子进程用法（探针自复用，工作流不直接调用）:
//   DeepSeek.Harness.Desktop.PayloadSmoke --case image <publish 目录> <库名>
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeepSeek.Harness.Desktop.PayloadSmoke;

internal static unsafe partial class Program
{
    private const int ChildTimeoutSeconds = 90;

    private static int Main(string[] args)
    {
        // 子进程模式：单用例，崩溃即非零退出，由父进程收集
        if (args.Length >= 2 && args[0] == "--case")
        {
            return RunCase(args[1], args.Skip(2).ToArray());
        }

        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: PayloadSmoke <publish-dir>");
            return 2;
        }

        string dir = Path.GetFullPath(args[0]);
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"FAIL: publish 目录不存在: {dir}");
            return 2;
        }

        Console.WriteLine($"== 载荷冒烟探针：{dir} ({RuntimeInformation.RuntimeIdentifier})");
        List<string> failures = [];
        CheckManagedPayload(dir, failures);
        if (OperatingSystem.IsWindows())
        {
            CheckWindows(dir, failures);
        }
        else
        {
            CheckUnix(dir, failures);
        }

        if (failures.Count > 0)
        {
            Console.Error.WriteLine($"FAIL: {failures.Count} 项：");
            foreach (string failure in failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }

            return 1;
        }

        Console.WriteLine("PASS: 载荷冒烟全部通过");
        return 0;
    }

    /// <summary>子进程入口：单用例执行。返回 0=通过；1=断言失败；非零异常退出由父进程定性。</summary>
    private static int RunCase(string name, string[] rest)
    {
        if (rest.Length < 2)
        {
            Console.Error.WriteLine("usage: --case image <publish-dir> <library>");
            return 2;
        }

        string dir = Path.GetFullPath(rest[0]);
        string library = rest[1];
        List<string> failures = [];
        switch (name)
        {
            case "image":
                RunImageDecode(dir, library, failures);
                break;
            default:
                Console.Error.WriteLine($"unknown case: {name}");
                return 2;
        }

        foreach (string failure in failures)
        {
            Console.Error.WriteLine($"  - {failure}");
        }

        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>以子进程执行功能用例：崩溃（含 native AV）、超时均记为失败项，不中断探针主流程。
    /// libraryPath 为父进程清单断言已解析的库绝对路径（根或 runtimes/&lt;rid&gt;/native），子进程不自行重探。</summary>
    private static void RunFunctionalCase(string name, string dir, string libraryPath, List<string> failures)
    {
        string host = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位探针可执行体");
        string assembly = typeof(Program).Assembly.Location;
        // 宿主为 dotnet muxer（framework-dependent 运行）时首个参数须为托管 dll 路径；
        // self-contained apphost 直跑时不插——apphost 不把首参解释为托管 dll
        bool viaMuxer = host.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) ||
                        host.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);
        ProcessStartInfo psi = new() { FileName = host };
        if (viaMuxer)
        {
            psi.ArgumentList.Add(assembly);
        }

        psi.ArgumentList.Add("--case");
        psi.ArgumentList.Add(name);
        psi.ArgumentList.Add(dir);
        psi.ArgumentList.Add(libraryPath);
        psi.RedirectStandardError = true;
        psi.RedirectStandardOutput = true;
        using var child = Process.Start(psi);
        if (child is null)
        {
            failures.Add($"{name} 用例子进程启动失败");
            return;
        }

        // 两路异步读：同步 ReadToEnd 会一直阻塞到子进程退出，超时分支永不可达；
        // 异步读后 WaitForExit 的超时才能真正兜住 native 挂死（诊断也还能拿到已吐的部分）。
        Task<string> stdoutTask = child.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(ChildTimeoutSeconds * 1000))
        {
            child.Kill(entireProcessTree: true);
            failures.Add($"{name} 用例子进程 {ChildTimeoutSeconds}s 超时（挂死）");
            return;
        }

        string stderr = stderrTask.GetAwaiter().GetResult();

        if (child.ExitCode != 0)
        {
            string detail = stderr.Trim();
            failures.Add(child.ExitCode is 1
                ? $"{name} 用例失败：{detail}"
                : $"{name} 用例子进程异常退出（exit=0x{child.ExitCode:X}，native 硬崩溃？）：{detail}");
            return;
        }

        Console.Write(stdoutTask.GetAwaiter().GetResult());
    }

    private static void CheckManagedPayload(string dir, List<string> failures)
    {
        if (!File.Exists(Path.Combine(dir, "DeepSeek.Harness.Desktop.dll")))
        {
            failures.Add("壳托管程序集缺失: DeepSeek.Harness.Desktop.dll");
        }
    }

    private static void CheckWindows(string dir, List<string> failures)
    {
        string[] libraries =
        [
            "libSkiaSharp.dll",
            "libHarfBuzzSharp.dll",
            "av_libglesv2.dll",
        ];
        Dictionary<string, nint> payload = CheckLibraryManifest(dir, libraries, failures, out Dictionary<string, string> resolvedPaths);
        CheckSkiaExports(payload, "libSkiaSharp.dll", failures);
        if (resolvedPaths.TryGetValue("libSkiaSharp.dll", out string? skiaPath))
        {
            RunFunctionalCase("image", dir, skiaPath, failures);
        }
    }

    private static void CheckUnix(string dir, List<string> failures)
    {
        string extension = OperatingSystem.IsMacOS() ? ".dylib" : ".so";
        string skia = $"libSkiaSharp{extension}";
        List<string> libraries = [skia, $"libHarfBuzzSharp{extension}"];
        if (OperatingSystem.IsMacOS())
        {
            libraries.Add("libAvaloniaNative.dylib");
        }

        Dictionary<string, nint> payload = CheckLibraryManifest(dir, libraries.ToArray(), failures, out Dictionary<string, string> resolvedPaths);
        CheckSkiaExports(payload, skia, failures);
        if (resolvedPaths.TryGetValue(skia, out string? skiaPath))
        {
            RunFunctionalCase("image", dir, skiaPath, failures);
        }
    }

    /// <summary>按 OS 断言原生库逐个存在于产物目录（根或 runtimes/&lt;rid&gt;/native，publish 落根、
    /// 自描述探测面留 runtimes），并 TryLoad 全部；resolvedPaths 输出各库已解析绝对路径，
    /// 供功能用例子进程直用（不重探）。</summary>
    private static Dictionary<string, nint> CheckLibraryManifest(string dir, string[] libraries, List<string> failures, out Dictionary<string, string> resolvedPaths)
    {
        string rid = RuntimeInformation.RuntimeIdentifier;
        Dictionary<string, nint> payload = new(StringComparer.Ordinal);
        resolvedPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in libraries)
        {
            string[] candidates =
            [
                Path.Combine(dir, name),
                Path.Combine(dir, "runtimes", rid, "native", name),
            ];
            string? path = candidates.FirstOrDefault(File.Exists);
            if (path is null)
            {
                failures.Add($"原生库缺失: {name}（探过: {string.Join(", ", candidates)}）");
                continue;
            }

            if (!NativeLibrary.TryLoad(path, out nint handle))
            {
                failures.Add($"原生库加载失败: {path}（缺依赖/rpath/执行位？）");
                continue;
            }

            payload[name] = handle;
            resolvedPaths[name] = path;
            Console.WriteLine($"  loaded: {name}");
        }

        return payload;
    }

    /// <summary>Skia 解码用例依赖的五个导出：任一缺失都必须在这里报红——子进程缺符号时静默退出，
    /// 若父进程不逐个断言，「导出漂移」会以 PASS 收场（假绿）。</summary>
    private static void CheckSkiaExports(Dictionary<string, nint> payload, string library, List<string> failures)
    {
        foreach (string export in s_skiaExports)
        {
            CheckExport(payload, library, export, failures);
        }
    }

    private static readonly string[] s_skiaExports =
    [
        "sk_data_new_from_file",
        "sk_data_unref",
        "sk_image_new_from_encoded",
        "sk_image_get_width",
        "sk_image_unref",
    ];

    private static void CheckExport(Dictionary<string, nint> payload, string library, string export, List<string> failures)
    {
        // 库缺失/加载失败已在清单断言报过，不重复计失败
        if (payload.TryGetValue(library, out nint handle) &&
            !NativeLibrary.TryGetExport(handle, export, out _))
        {
            failures.Add($"导出符号缺失: {library}!{export}");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint SkDataNewFromFile(sbyte* path);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint SkImageNewFromEncoded(nint data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SkImageGetWidth(nint image);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SkUnref(nint handle);

    /// <summary>经 Skia C API 真解码产物内 icon.png（文件读取 → PNG 解码 → 取宽），穿透 libSkiaSharp
    /// 的 native 路径——壳渲染栈缺符号/ABI 漂移在此现形。仅子进程内调用；
    /// skiaLibraryPath 为父进程已解析的库绝对路径。</summary>
    private static void RunImageDecode(string dir, string skiaLibraryPath, List<string> failures)
    {
        if (!NativeLibrary.TryLoad(skiaLibraryPath, out nint skia))
        {
            return; // 缺库在父进程清单断言已报过，此处静默退出（子进程 exit 0）
        }

        if (!NativeLibrary.TryGetExport(skia, "sk_data_new_from_file", out nint newDataSymbol) ||
            !NativeLibrary.TryGetExport(skia, "sk_data_unref", out nint dataUnrefSymbol) ||
            !NativeLibrary.TryGetExport(skia, "sk_image_new_from_encoded", out nint newImageSymbol) ||
            !NativeLibrary.TryGetExport(skia, "sk_image_get_width", out nint getWidthSymbol) ||
            !NativeLibrary.TryGetExport(skia, "sk_image_unref", out nint imageUnrefSymbol))
        {
            return; // 缺导出已在父进程导出断言报过
        }

        string iconPath = Path.Combine(dir, "icon.png");
        if (!File.Exists(iconPath))
        {
            failures.Add("icon.png 缺失（产物 Content 未随 publish 落盘？）");
            return;
        }

        SkDataNewFromFile newFromFile = Marshal.GetDelegateForFunctionPointer<SkDataNewFromFile>(newDataSymbol);
        SkUnref dataUnref = Marshal.GetDelegateForFunctionPointer<SkUnref>(dataUnrefSymbol);
        SkImageNewFromEncoded newFromEncoded = Marshal.GetDelegateForFunctionPointer<SkImageNewFromEncoded>(newImageSymbol);
        SkImageGetWidth getWidth = Marshal.GetDelegateForFunctionPointer<SkImageGetWidth>(getWidthSymbol);
        SkUnref imageUnref = Marshal.GetDelegateForFunctionPointer<SkUnref>(imageUnrefSymbol);

        using Utf8String nativePath = Utf8(iconPath);
        nint data = newFromFile(nativePath.Ptr);
        if (data == nint.Zero)
        {
            failures.Add($"sk_data_new_from_file 返回空: {iconPath}（libSkiaSharp 文件读取路径不可用？）");
            return;
        }

        nint image;
        try
        {
            image = newFromEncoded(data);
        }
        finally
        {
            dataUnref(data);
        }

        if (image == nint.Zero)
        {
            failures.Add("icon.png 解码失败（sk_image_new_from_encoded 返回空）——libSkiaSharp 编解码路径不可用？");
            return;
        }

        int width;
        try
        {
            width = getWidth(image);
        }
        finally
        {
            imageUnref(image);
        }

        if (width <= 0)
        {
            failures.Add($"icon.png 解码后宽度非法: {width}");
            return;
        }

        Console.WriteLine($"  decoded: icon.png ({width}px 宽, {new FileInfo(iconPath).Length} bytes)");
    }

    /// <summary>单串 UTF8 原生内存（含终止符），Dispose 释放。</summary>
    private readonly struct Utf8String(sbyte* ptr, nint block) : IDisposable
    {
        public sbyte* Ptr { get; } = ptr;

        public void Dispose() => Marshal.FreeHGlobal(block);
    }

    private static Utf8String Utf8(string value)
    {
        int byteCount = Encoding.UTF8.GetMaxByteCount(value.Length) + 1;
        nint block = Marshal.AllocHGlobal(byteCount);
        fixed (char* source = value)
        {
            int written = Encoding.UTF8.GetBytes(source, value.Length, (byte*)block, byteCount - 1);
            ((byte*)block)[written] = 0;
        }

        return new Utf8String((sbyte*)block, block);
    }
}
