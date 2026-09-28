// PayloadSmoke — 打包期载荷冒烟探针（ADR testing/2026-09-13-payload-smoke-probe）。
// 用法（父进程，随 package 工作流在 publish 后调用）:
//   DeepSeek.Harness.Desktop.PayloadSmoke <publish 目录>
// 对自包含 publish 产物的 native 载荷面冒烟：清单存在性 → FFI 加载+导出解析 →
// 功能用例（图像解码 saucer_icon_new_from_file、PTY ryn_pty_spawn / ConPTY）。
// 功能用例在**子进程**中执行：native 侧硬崩溃/挂死被记为失败项并继续其余用例，
// 诊断清单（逐条列因，exit 1）不被单点崩溃截断。
// 子进程用法（探针自复用，工作流不直接调用）:
//   DeepSeek.Harness.Desktop.PayloadSmoke --case image|pty <publish 目录> <库名>
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
            Console.Error.WriteLine("usage: --case image|pty <publish-dir> <library>");
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
            case "pty":
                RunUnixPty(library, failures);
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

        // 子进程 stdout 仅数行；若未来用例输出接近管道缓冲（~64KB），须改异步双流读，否则先堵后超时误判挂死
        string stderr = child.StandardError.ReadToEnd();
        if (!child.WaitForExit(ChildTimeoutSeconds * 1000))
        {
            child.Kill(entireProcessTree: true);
            failures.Add($"{name} 用例子进程 {ChildTimeoutSeconds}s 超时（挂死）");
            return;
        }

        if (child.ExitCode != 0)
        {
            string detail = stderr.Trim();
            failures.Add(child.ExitCode is 1
                ? $"{name} 用例失败：{detail}"
                : $"{name} 用例子进程异常退出（exit=0x{child.ExitCode:X}，native 硬崩溃？）：{detail}");
            return;
        }

        Console.Write(child.StandardOutput.ReadToEnd());
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
            "WebView2Loader.dll",
            "saucer.dll",
            "saucer-bindings.dll",
            "saucer-bindings-desktop.dll",
        ];
        Dictionary<string, nint> payload = CheckLibraryManifest(dir, libraries, failures, out Dictionary<string, string> resolvedPaths);
        CheckExport(payload, "WebView2Loader.dll", "GetAvailableCoreWebView2BrowserVersionString", failures);
        CheckExport(payload, "saucer-bindings.dll", "saucer_icon_new_from_file", failures);
        RunFunctionalCase("image", dir, resolvedPaths["saucer-bindings.dll"], failures);
        CheckConPty(failures);
    }

    private static void CheckUnix(string dir, List<string> failures)
    {
        string extension = OperatingSystem.IsMacOS() ? ".dylib" : ".so";
        string[] libraries =
        [
            $"libsaucer{extension}",
            $"libsaucer-bindings{extension}",
            $"libsaucer-bindings-desktop{extension}",
            $"libryn-pty{extension}",
        ];
        Dictionary<string, nint> payload = CheckLibraryManifest(dir, libraries, failures, out Dictionary<string, string> resolvedPaths);
        CheckExport(payload, $"libsaucer-bindings{extension}", "saucer_icon_new_from_file", failures);
        CheckExport(payload, $"libryn-pty{extension}", "ryn_pty_spawn", failures);
        RunFunctionalCase("image", dir, resolvedPaths[$"libsaucer-bindings{extension}"], failures);
        RunFunctionalCase("pty", dir, resolvedPaths[$"libryn-pty{extension}"], failures);
    }

    /// <summary>按 OS 断言原生库逐个存在于产物目录（根或 runtimes/&lt;rid&gt;/native，同 Ryn NativeLibraryResolver 探测序），并 TryLoad 全部；
    /// resolvedPaths 输出各库已解析绝对路径，供功能用例子进程直用（不重探）。</summary>
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
    private delegate nint SaucerIconNewFromFile(sbyte* path, int* error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SaucerIconFree(nint icon);

    /// <summary>经 saucer-bindings 对产物内 icon.png 真解码（调用同时穿透 libsaucer 原生解码路径）。仅子进程内调用；
    /// bindingsLibraryPath 为父进程已解析的库绝对路径。</summary>
    private static void RunImageDecode(string dir, string bindingsLibraryPath, List<string> failures)
    {
        if (!NativeLibrary.TryLoad(bindingsLibraryPath, out nint bindings))
        {
            return; // 缺库在父进程清单断言已报过，此处静默退出（子进程 exit 0）
        }

        if (!NativeLibrary.TryGetExport(bindings, "saucer_icon_new_from_file", out nint newSymbol) ||
            !NativeLibrary.TryGetExport(bindings, "saucer_icon_free", out nint freeSymbol))
        {
            return; // 缺导出已在父进程导出断言报过
        }

        string iconPath = Path.Combine(dir, "icon.png");
        if (!File.Exists(iconPath))
        {
            failures.Add("icon.png 缺失（产物 Content 未随 publish 落盘？）");
            return;
        }

        SaucerIconNewFromFile newFromFile = Marshal.GetDelegateForFunctionPointer<SaucerIconNewFromFile>(newSymbol);
        SaucerIconFree free = Marshal.GetDelegateForFunctionPointer<SaucerIconFree>(freeSymbol);
        using Utf8String nativePath = Utf8(iconPath);
        int error = 0;
        nint handle = newFromFile(nativePath.Ptr, &error);
        if (handle == nint.Zero || error != 0)
        {
            // 当前 ABI 下 error 非零则句柄为 null；非零句柄仍释放，防御未来 ABI 演进
            if (handle != nint.Zero)
            {
                free(handle);
            }

            failures.Add($"图像解码失败: icon.png（error={error}, 有句柄={handle != nint.Zero}）——libsaucer 原生解码路径不可用？");
            return;
        }

        free(handle);
        Console.WriteLine($"  decoded: icon.png ({new FileInfo(iconPath).Length} bytes)");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RynPtySpawn(sbyte* command, sbyte** argv, sbyte** envp, sbyte* cwd, ushort cols, ushort rows, int* masterFd, int* childPid);

    /// <summary>经 ryn-pty fork 真伪终端跑 `sh -c echo` 并读回 token（fork/exec/read/waitpid 全链）。仅子进程内调用；
    /// ptyLibraryPath 为父进程已解析的库绝对路径。</summary>
    private static void RunUnixPty(string ptyLibraryPath, List<string> failures)
    {
        if (!NativeLibrary.TryLoad(ptyLibraryPath, out nint library) ||
            !NativeLibrary.TryGetExport(library, "ryn_pty_spawn", out nint symbol))
        {
            return; // 缺库/缺导出已在父进程断言报过
        }

        const string token = "payload-smoke-pty-ok";
        RynPtySpawn spawn = Marshal.GetDelegateForFunctionPointer<RynPtySpawn>(symbol);
        using Utf8String command = Utf8("/bin/sh");
        using Utf8PointerArray argv = Utf8Array(["/bin/sh", "-c", $"echo {token}", null]);
        using Utf8PointerArray envp = Utf8Array(["PATH=/usr/bin:/bin", null]);
        int masterFd = -1;
        int childPid = -1;
        int rc = spawn(command.Ptr, argv.Ptr, envp.Ptr, null, 80, 24, &masterFd, &childPid);
        if (rc != 0)
        {
            failures.Add($"ryn_pty_spawn 失败: rc={rc}（forkpty 不可用？）");
            return;
        }

        string output = ReadAll(masterFd);
        _ = waitpid(childPid, out _, 0);
        if (!output.Contains(token, StringComparison.Ordinal))
        {
            failures.Add($"PTY 回读缺 token（输出 {output.Length} 字节: {output.Trim()}）");
        }
        else
        {
            Console.WriteLine($"  pty: {output.Trim()}");
        }
    }

    /// <summary>原始 read 循环读 pty 主端：0=EOF，-1（含子进程退出后 Linux 的 EIO）都视为流结束。
    /// 返回值按上游 ABI 为 ssize_t，此处 int 截断不可能发生（buffer 4096）。</summary>
    private static string ReadAll(int fd)
    {
        byte[] buffer = new byte[4096];
        StringBuilder output = new();
        fixed (byte* p = buffer)
        {
            while (true)
            {
                int n = read(fd, p, buffer.Length);
                if (n <= 0)
                {
                    break;
                }

                _ = output.Append(Encoding.UTF8.GetString(buffer, 0, n));
            }
        }

        return output.ToString();
    }

    private static void CheckConPty(List<string> failures)
    {
        if (!CreatePipe(out nint readPipe, out nint writePipe, nint.Zero, 0) ||
            !CreatePipe(out nint refReadPipe, out nint refWritePipe, nint.Zero, 0))
        {
            failures.Add("CreatePipe 失败（ConPTY 探针前置）");
            return;
        }

        int hr = CreatePseudoConsole(new Coord { X = 80, Y = 24 }, readPipe, refWritePipe, 0, out nint hpc);
        if (hr != 0)
        {
            failures.Add($"CreatePseudoConsole 失败: HRESULT=0x{hr:X8}");
        }
        else
        {
            ClosePseudoConsole(hpc);
            Console.WriteLine("  conpty: ok");
        }
    }

    /// <summary>单串 UTF8 原生内存（含终止符），Dispose 释放。</summary>
    private readonly struct Utf8String(sbyte* ptr, nint block) : IDisposable
    {
        public sbyte* Ptr { get; } = ptr;

        public void Dispose() => Marshal.FreeHGlobal(block);
    }

    private static unsafe Utf8String Utf8(string value)
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

    /// <summary>UTF8 串指针数组（末位 null 由调用方传 null 项表达），Dispose 释放全部块。</summary>
    private readonly struct Utf8PointerArray(sbyte** ptr, List<nint> blocks) : IDisposable
    {
        public sbyte** Ptr { get; } = ptr;

        public void Dispose()
        {
            foreach (nint block in blocks)
            {
                Marshal.FreeHGlobal(block);
            }
        }
    }

    private static unsafe Utf8PointerArray Utf8Array(IReadOnlyList<string?> values)
    {
        List<nint> blocks = [];
        nint arrayBlock = Marshal.AllocHGlobal(nint.Size * values.Count);
        blocks.Add(arrayBlock);
        sbyte** array = (sbyte**)arrayBlock;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is null)
            {
                array[i] = null;
                continue;
            }

            Utf8String item = Utf8(values[i]!);
            blocks.Add((nint)item.Ptr);
            array[i] = item.Ptr;
        }

        return new Utf8PointerArray(array, blocks);
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int waitpid(int pid, out int status, int options);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int read(int fd, byte* buffer, int count);

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out nint readPipe, out nint writePipe, nint attrs, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CreatePseudoConsole(Coord size, nint input, nint output, uint flags, out nint hpc);

    [LibraryImport("kernel32.dll")]
    private static partial void ClosePseudoConsole(nint hpc);
}
