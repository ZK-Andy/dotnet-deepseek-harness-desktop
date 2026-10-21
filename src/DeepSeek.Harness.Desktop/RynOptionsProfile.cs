using Ryn.Core;

namespace DeepSeek.Harness.Desktop;

/// <summary>
/// Ryn 应用装配选项（ADR 组合根寿命与配置单一来源 D7）：Ryn 适配面里唯一持续增长的一块——窗口行为、
/// 代理源白名单、图标、调试器开关都属声明式配置。住在接线层之外（同属装配面，C2 方法清单覆盖），
/// 让新增选项占自己的方法预算，而非装配面的。
/// </summary>
internal static class RynOptionsProfile
{
    /// <summary>把桌面壳的装配选项写入 Ryn 构建器。</summary>
    /// <param name="opts">Ryn 选项对象（<c>ConfigureOptions</c> 回调参数）。</param>
    /// <param name="preflight">容器之前的头部产出（dev 判定与 A 类启动配置）。</param>
    /// <param name="proxy">回环代理源；null = 绑定失败，降级 wwwroot 占位。</param>
    /// <param name="captionBar">自绘标题栏参数家（拖拽条高度）。</param>
    /// <param name="iconPath">图标路径（托盘与窗口共用；缺失时留空）。</param>
    internal static void Apply(
        RynOptions opts,
        Preflight preflight,
        DshLoopbackProxy? proxy,
        CaptionBarOptions captionBar,
        string iconPath)
    {
        // IPC 桥接白名单显式登记代理源（Ryn 默认只认 ryn://app；Ryn dev-server 分支会自动
        // 追加代理源 + IPC 服地址，此处显式更稳，不依赖隐式追加）。代理源恒为初始 URL
        // （dsh 未就绪时窗口即开 holder；Ryn dev-server 分支在窗口创建时接管 IPC，不依赖
        // dsh 时序——冷机探针同样有效）。仅代理绑定失败才回退 wwwroot 占位（极罕见）。
        if (proxy is not null)
        {
            opts.AllowedOrigins.Add(proxy.Origin);
            opts.Url = proxy.Url;
        }
        else
        {
            // 降级：代理未起时展示本地占位页，保证壳仍可开
            opts.ContentDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        }

        opts.Title = "DeepSeek Harness Desktop";
        opts.Width = 1200;
        opts.Height = 800;
        // 无边框窗口（ADR frameless-uniform-caption-bar）：Frameless 三端去原生 chrome；macOS 观感
        // 由壳自绘（注入脚本画红绿灯 + 侧栏顶部让位，见 PageBridge.CaptionBar），拖拽条与 chrome
        // 同高。禁用 Overlay/TrafficLightPosition（原生红绿灯，mac x64 Rosetta segfault）与任何
        // 宿主冒充（data-platform 会令 dsh 客户端插件加载失败）。
        opts.TitleBarStyle = TitleBarStyle.Frameless;
        opts.TitleBarAutoDragHeight = captionBar.HeightPx;
        opts.Backdrop = MacNativeChrome.IsEnabled ? BackdropMaterial.Blur : BackdropMaterial.None; // macOS 毛玻璃（官方 vibrancy）；闸外恒 None（判据单点见 MacNativeChrome）
        opts.PersistWindowState = true; // 几何持久化：rationale 见 ADR window-geometry-ryn-native-persist
        // Linux 帧传递路径不在此赋值：Ryn 的 Build 从 appsettings 的 Ryn:LinuxRenderingMode 绑定，回调再写一遍会压掉那个框架键
        // A 类启动配置经类型化值消费（批次 3）：dev 后缀规则封装进 LaunchOptions。
        opts.ApplicationId = preflight.Launch.ApplicationIdFor("io.github.ZK-Andy.dotnet-deepseek-harness-desktop");
        if (File.Exists(iconPath)) // verify-code-conventions: ignore 装配面：icon 探测是配置面
        {
            opts.IconPath = iconPath;
        }
        // 缺失不告警分支：icon 缺失由下方 Ryn opts 摘要行的 Icon=missing 留痕（托盘侧 IsReady 同样降级）

        HostLog.Write($"[host] Ryn opts: Url={(proxy?.Url?.ToString() ?? "null")} ApplicationId={opts.ApplicationId} LinuxRendering={opts.LinuxRenderingMode} Icon={(File.Exists(iconPath) ? iconPath : "missing")}"); // verify-code-conventions: ignore 装配面：icon 探测是配置面
        // WebView 调试器默认关闭；开发期设 DSH_DEVTOOLS=1 开启（与 dev 判定无关，
        // 打包产品形态下同样生效）——判定随 A 类启动配置在 LaunchOptions.Resolve 单点解析
        // （ADR post-restructure-ledger-batch）。
        opts.DevTools = preflight.Launch.DevTools;
    }
}
