namespace DeepSeek.Harness.Desktop.Core.Localization;

/// <summary>
/// UI 语言持久化端口（R3：接口在 Core、文件实现进 Infrastructure、组合根注入）：
/// 记住上一次 dsh 上报的语言，供<b>下次启动</b>在 dsh 起来之前使用。
/// </summary>
/// <remarks>
/// 存在理由：引导页/进度页在 dsh 启动前渲染，那时 companion 还没跑、拿不到 dsh 语言；
/// 没有持久值就只能退回 OS locale，而 dsh 语言设置独立于 OS locale（用户在中文系统里切英文）。
/// 读写都是增强能力：任何失败都不得影响启动或语言切换。
/// </remarks>
public interface IUiLocaleStore
{
    /// <summary>读取上次记住的 locale（如 <c>en</c>/<c>zh-CN</c>）；无记录、不可读或内容损坏时返回 null。</summary>
    /// <returns>上次 locale；不可用时 null（调用方回退 OS locale）。</returns>
    string? Load();

    /// <summary>记住本次 locale（尽力而为；写失败仅导致下次启动回退 OS locale）。</summary>
    /// <param name="locale">本次生效的 locale 原值。</param>
    void Save(string locale);
}
