using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Infrastructure.Tests;

/// <summary>
/// dsh 启动命令的平台形状断言（ADR windows-dsh-spawn-unified 的测试面）：Unix 直接跑 <c>dsh</c>，
/// Windows 经 <c>cmd.exe /d /s /c dsh.cmd</c> 中转（<c>UseShellExecute=false</c> 的 CreateProcess
/// 对裸名不走 PATHEXT）。两个入口共用同一套前缀判据，只差是否核对尾随参数。
/// </summary>
internal static class DshSpawnAssert
{
    /// <summary>断言可执行名与（Windows）中转前缀，不核对 dsh 参数。</summary>
    /// <param name="psi">被测启动信息。</param>
    public static void Exe(ProcessStartInfo psi) => Core(psi, expectedArgs: null);

    /// <summary>断言可执行名与参数表完整形状（Windows 含 <c>cmd.exe</c> 中转前缀与 <c>dsh.cmd</c> 垫片）。</summary>
    /// <param name="psi">被测启动信息。</param>
    /// <param name="expectedArgs">平台无关的 dsh 参数表。</param>
    public static void ExeAndArgs(ProcessStartInfo psi, params string[] expectedArgs) => Core(psi, expectedArgs);

    private static void Core(ProcessStartInfo psi, string[]? expectedArgs)
    {
        string[] actual = [.. psi.ArgumentList];
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cmd.exe", psi.FileName);
            string[] command = ["/d", "/s", "/c", RuntimeBootstrap.DshShimPath(null, windows: true)];
            if (expectedArgs is null)
            {
                Assert.Equal(command, actual.Take(command.Length).ToArray());
                return;
            }

            Assert.Equal([.. command, .. expectedArgs], actual);
            return;
        }

        Assert.Equal("dsh", psi.FileName);
        if (expectedArgs is not null)
        {
            Assert.Equal(expectedArgs, actual);
        }
    }
}
