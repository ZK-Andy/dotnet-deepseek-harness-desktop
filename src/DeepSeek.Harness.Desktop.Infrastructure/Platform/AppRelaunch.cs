using System.Diagnostics;

namespace DeepSeek.Harness.Desktop.Infrastructure.Platform;

/// <summary>
/// 应用重启拉起新实例（ADR app-restart-native-switch）：按 <see cref="Environment.ProcessPath"/>
/// + 原始命令行参数派生自身，进程分离（不等待、不随本进程退出）。与 <see cref="Autostart"/> 同理
/// 取 <see cref="Environment.ProcessPath"/>——自更新原地升级不改路径，无需跟踪；原始参数透传保留
/// dev 隔离开关等启动形态。调用时点契约：宿主侧须先回收 dsh 树并释放单实例仲裁位
/// （<c>ExitPipeline.Restart</c> 的次序保证），再触发本方法。
/// </summary>
public static class AppRelaunch
{
    /// <summary>派生本进程的新实例（原始参数原样透传）。失败 loud：定位不到可执行文件或
    /// spawn 抛出直接上抛，调用方决定是否留痕——重启失败静默会把用户带进「点了没反应」盲区。</summary>
    public static void SpawnSelf()
    {
        string exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法定位当前可执行文件路径，无法重启");
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
        };
        // 跳过 argv[0]：框架依赖形态（dotnet app.dll …）下 FileName 是 dotnet、argv[1] 是 dll 路径，
        // apphost 形态下 argv[1..] 就是原始参数——两种形态透传后语义都等价于 launcher 再启动一次。
        foreach (string arg in Environment.GetCommandLineArgs().Skip(1))
        {
            psi.ArgumentList.Add(arg);
        }

        Process.Start(psi);
    }
}
