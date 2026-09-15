namespace DeepSeek.Harness.Desktop.Core.Bootstrap;

/// <summary>引导进度步骤（引导页步骤序的单一事实源；跨 Core/Infrastructure/Presentation 的端口值类型）。</summary>
public enum BootstrapStep
{
    /// <summary>确保系统全局 Node（PATH）可用；无则下载装到系统全局位。</summary>
    EnsureNode,

    /// <summary>经全局 npm 把 dsh 装到系统全局位（装 / 更新到 @alpha）。</summary>
    InstallDsh,

    /// <summary>验证 <c>dsh --version</c> 可解析（全局 dsh 就位）。</summary>
    VerifyDsh,

    /// <summary>插件引导（ADR reference-alignment 批次二）：运行时就位后的可选插件确认/安装相。
    /// 仅作引导页步骤呈现——实际交互由 <c>dsh-desktop-preinstall</c> 事件驱动，
    /// RuntimeBootstrap 的步骤机不产出此值。</summary>
    PreinstallPlugins,

    /// <summary>引导完成。</summary>
    Ready,
}
