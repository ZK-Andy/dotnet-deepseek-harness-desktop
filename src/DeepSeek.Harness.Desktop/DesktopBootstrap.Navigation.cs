using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using System.Threading.Tasks;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop;

/// <summary>
/// 组合根的 WebView 导航辅助（分部 <see cref="DesktopBootstrap"/>）：origin 授权与
/// 「导航并等待提交」原语。供引导完成两跳与监督器恢复导航共用（ADR
/// bootstrap-cross-scheme-cookie-401 与 ryn-0-38-bump-authorize-ipc-origin）。
/// </summary>
public sealed partial class DesktopBootstrap
{
    /// <summary>导航前授权 <paramref name="url"/> 的 origin 可 IPC（Ryn 0.38 受信 origin 集合，ADR
    /// ryn-pr91-trusted-origin）：端口漂移/引导后首次进入 dsh 时页面 origin 不在初始受信集里，
    /// 未授权则页面命令通道被拒（token 有效也拒）。幂等，重复授权无害；授权在导航前生效，
    /// 「bridge 随下一次导航安装」。窗口未就绪时异常照抛（fail loud，调用面均已在窗口就绪后）。</summary>
    private void AuthorizeIpcOriginFor(Uri url)
    {
        string origin = url.GetLeftPart(UriPartial.Authority);
        _windowAccessor.Current.AuthorizeIpcOrigin(origin);
        Services.HostLog.Write($"[nav] 已授权 IPC origin：{origin}");
    }

    /// <summary>导航并等待其真正提交（<see cref="Services.RynNavigationCallbacks"/> 的
    /// 「导航已到达」信号，先订阅后导航避免错过）。提交信号用于隔开两跳导航——
    /// <c>NavigateAsync</c> 连发会被 WebKitGTK 合并，前一跳尚未发出即被后一跳覆盖。
    /// 等待超时按「已提交」降级继续（信号只是隔跳手段，缺位时不比单跳直导更差）；
    /// 取消（应用退出）照常传播。</summary>
    private async Task NavigateAndAwaitCommitAsync(Uri target, CancellationToken ct)
    {
        Services.RynNavigationCallbacks callbacks =
            _app.Services.GetRequiredService<Services.RynNavigationCallbacks>();
        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        callbacks.SetOnNavigated(() => arrived.TrySetResult());
        try
        {
            await _windowAccessor.Current.NavigateAsync(target);
            try
            {
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (TimeoutException)
            {
                Services.HostLog.Write("[nav] 等待导航提交信号超时（5s），按已提交继续");
            }
        }
        finally
        {
            callbacks.SetOnNavigated(static () => { });
        }
    }

}
