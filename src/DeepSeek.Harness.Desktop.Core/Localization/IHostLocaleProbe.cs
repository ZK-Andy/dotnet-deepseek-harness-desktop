namespace DeepSeek.Harness.Desktop.Core.Localization;

/// <summary>
/// 宿主 locale 探测端口（ADR core-purity-boundaries）：读环境变量族与 <c>CurrentUICulture</c> 是进程环境面，
/// Core 只经本端口取初值；实现在 Infrastructure（Core 不再自己读环境变量）。
/// </summary>
public interface IHostLocaleProbe
{
    /// <summary>探测宿主 locale（如 <c>zh-CN</c>/<c>en-US</c>）。</summary>
    /// <returns>locale 标记；探测不出返回 null（调用方按产品缺省处理）。</returns>
    string? Detect();
}
