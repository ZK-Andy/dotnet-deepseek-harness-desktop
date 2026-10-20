namespace DeepSeek.Harness.Desktop.Core.Platform;

/// <summary>
/// 系统默认浏览器端口（R3 边界抽象；ADR host-boundary-ports）：外部链接打开是进程外部交互，
/// 壳侧只经本端口触发——不直调基础设施静态面（xdg-open / shell execute 留在实现侧）。
/// </summary>
public interface ISystemBrowser
{
    /// <summary>用系统默认浏览器打开 URL。</summary>
    /// <param name="url">待打开的绝对 http(s) URL（调用方已做策略校验）。</param>
    /// <returns>是否已打开（已启动，或已成功交棒既有实例）。</returns>
    bool Open(string url);
}
