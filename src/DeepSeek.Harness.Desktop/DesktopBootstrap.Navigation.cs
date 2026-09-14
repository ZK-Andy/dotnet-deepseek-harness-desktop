using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace DeepSeek.Harness.Desktop;

/// <summary>
/// 组合根的 WebView 导航辅助（分部 <see cref="DesktopBootstrap"/>）：origin 授权与
/// 「导航并等待提交」原语。供引导完成两跳与监督器恢复导航共用（ADR
/// bootstrap-cross-scheme-cookie-401 与 ryn-0-38-bump-authorize-ipc-origin）。
/// </summary>
public sealed partial class DesktopBootstrap
{
    /// <summary>引导完成后的壳侧导航收尾（ADR bootstrap-cross-scheme-cookie-401）：记录 webUrl 后
    /// WebKitGTK 两跳导航进主界面，由引导服务在 dsh 就位时回调。</summary>
    /// <param name="app">Ryn 应用装配产出（窗口访问器与回调服务来源）。</param>
    /// <param name="url">dsh 就位端点。</param>
    /// <param name="ct">引导任务取消令牌。</param>
    private async Task EnterMainUiAsync(AppSetup app, DshWebUrl url, CancellationToken ct)
    {
        _webUrl = url.Value;
        // WebKitGTK 两跳导航：从自定义 scheme 占位页（ryn://app）发起的跨 scheme 导航链上，
        // dsh 的 SameSite=Strict 会话 cookie 不随 303 回环重定向发送（沙箱实锤 2026-09-14：
        // mint 命中 → 随后 GET / 无 cookie 401）。先落裸 origin http 页脱离 ryn:// 链路
        // （该跳无 token 必得 401，瞬时无害），再从 http 页发起同站导航——Strict cookie 正常随行。
        // 第二跳必须等第一跳真正提交（NavigateAsync 连发会被 WebKitGTK 合并成一次导航）。
        Uri landing = url.AuthorityRoot;
        AuthorizeIpcOriginFor(app.WindowAccessor, landing);
        await NavigateAndAwaitCommitAsync(app, landing, ct);
        await app.WindowAccessor.Current.NavigateAsync(url.Value);
    }

    /// <summary>导航前授权 <paramref name="url"/> 的 origin 可 IPC（Ryn 0.38 受信 origin 集合，ADR
    /// ryn-pr91-trusted-origin）：端口漂移/引导后首次进入 dsh 时页面 origin 不在初始受信集里，
    /// 未授权则页面命令通道被拒（token 有效也拒）。幂等，重复授权无害；授权在导航前生效，
    /// 「bridge 随下一次导航安装」。窗口未就绪时异常照抛（fail loud，调用面均已在窗口就绪后）。</summary>
    /// <param name="accessor">当前窗口访问器。</param>
    /// <param name="url">待授权 URL。</param>
    private void AuthorizeIpcOriginFor(CurrentWindowAccessor accessor, Uri url)
    {
        string origin = url.GetLeftPart(UriPartial.Authority);
        accessor.Current.AuthorizeIpcOrigin(origin);
        HostLog.Write($"[nav] 已授权 IPC origin：{origin}");
    }

    /// <summary>导航并等待其真正提交（<see cref="Services.RynNavigationCallbacks"/> 的
    /// 「导航已到达」信号，先订阅后导航避免错过）。提交信号用于隔开两跳导航——
    /// <c>NavigateAsync</c> 连发会被 WebKitGTK 合并，前一跳尚未发出即被后一跳覆盖。
    /// 等待超时按「已提交」降级继续（信号只是隔跳手段，缺位时不比单跳直导更差）；
    /// 取消（应用退出）照常传播。</summary>
    /// <param name="app">Ryn 应用装配产出（导航回调服务来源）。</param>
    /// <param name="target">导航靶点。</param>
    /// <param name="ct">引导任务取消令牌。</param>
    private async Task NavigateAndAwaitCommitAsync(AppSetup app, Uri target, CancellationToken ct)
    {
        Services.RynNavigationCallbacks callbacks =
            app.App.Services.GetRequiredService<Services.RynNavigationCallbacks>();
        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        callbacks.SetOnNavigated(() => arrived.TrySetResult());
        try
        {
            await app.WindowAccessor.Current.NavigateAsync(target);
            try
            {
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (TimeoutException)
            {
                HostLog.Write("[nav] 等待导航提交信号超时（5s），按已提交继续");
            }
        }
        finally
        {
            callbacks.SetOnNavigated(static () => { });
        }
    }
}
