// PayloadSmoke — 打包期载荷冒烟探针（ADR testing/2026-09-13-payload-smoke-probe）。
// 用法: dotnet DeepSeek.Harness.Desktop.PayloadSmoke.dll <publish 目录>
// 对自包含 publish 产物的 native 载荷面冒烟：清单存在性 → FFI 加载+导出解析 →
// 图像解码（saucer_icon_new_from_file）→ PTY 功能性（ryn_pty_spawn / ConPTY）。
// 任一断言失败即 fail loud（逐条列因，exit 1）。
using System.Runtime.InteropServices;
using System.Text;

namespace DeepSeek.Harness.Desktop.PayloadSmoke;

internal static unsafe partial class Program
{
    private static int Main(string[] args)
    {
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
        Dictionary<string, nint> payload = CheckLibraryManifest(dir, libraries, failures);
        CheckExport(payload, "WebView2Loader.dll", "GetAvailableCoreWebView2BrowserVersionString", failures);
        CheckExport(payload, "saucer-bindings.dll", "saucer_icon_new_from_file", failures);
        CheckImageDecode(dir, "saucer-bindings.dll", payload, failures);
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
        Dictionary<string, nint> payload = CheckLibraryManifest(dir, libraries, failures);
        CheckExport(payload, $"libsaucer-bindings{extension}", "saucer_icon_new_from_file", failures);
        CheckExport(payload, $"libryn-pty{extension}", "ryn_pty_spawn", failures);
        CheckImageDecode(dir, $"libsaucer-bindings{extension}", payload, failures);
        CheckUnixPty(payload, $"libryn-pty{extension}", failures);
    }

    /// <summary>按 OS 断言原生库逐个存在于产物目录（根或 runtimes/&lt;rid&gt;/native，同 Ryn NativeLibraryResolver 探测序），并 TryLoad 全部。</summary>
    private static Dictionary<string, nint> CheckLibraryManifest(string dir, string[] libraries, List<string> failures)
    {
        string rid = RuntimeInformation.RuntimeIdentifier;
        Dictionary<string, nint> payload = new(StringComparer.Ordinal);
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

    /// <summary>经 saucer-bindings 对产物内 icon.png 真解码（调用同时穿透 libsaucer 原生解码路径）。</summary>
    private static void CheckImageDecode(string dir, string bindingsLibrary, Dictionary<string, nint> payload, List<string> failures)
    {
        if (!payload.TryGetValue(bindingsLibrary, out nint bindings) ||
            !NativeLibrary.TryGetExport(bindings, "saucer_icon_new_from_file", out nint newSymbol) ||
            !NativeLibrary.TryGetExport(bindings, "saucer_icon_free", out nint freeSymbol))
        {
            return; // 缺库/缺导出已在清单与导出断言报过
        }

        string iconPath = Path.Combine(dir, "icon.png");
        if (!File.Exists(iconPath))
        {
            failures.Add("icon.png 缺失（产物 Content 未随 publish 落盘？）");
            return;
        }

        SaucerIconNewFromFile newFromFile = Marshal.GetDelegateForFunctionPointer<SaucerIconNewFromFile>(newSymbol);
        SaucerIconFree free = Marshal.GetDelegateForFunctionPointer<SaucerIconFree>(freeSymbol);
        using Utf8String path = Utf8(iconPath);
        int error = 0;
        nint handle = newFromFile(path.Ptr, &error);
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

    /// <summary>经 ryn-pty fork 真伪终端跑 `sh -c echo` 并读回 token（fork/exec/read/waitpid 全链）。</summary>
    private static void CheckUnixPty(Dictionary<string, nint> payload, string ptyLibrary, List<string> failures)
    {
        if (!payload.TryGetValue(ptyLibrary, out nint library) ||
            !NativeLibrary.TryGetExport(library, "ryn_pty_spawn", out nint symbol))
        {
            return; // 缺库/缺导出已在清单与导出断言报过
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

    // 一次性探针进程：管道句柄与 pty master fd 均不显式关闭，exit 即 OS 回收；
    // 若将本探针逻辑搬入长活进程，须先补齐 CloseHandle/Close 清理。
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
