using Ryn.Core;

namespace DeepSeek.Harness.Desktop.PageBridge;

/// <summary>
/// <see cref="IWindowScriptTarget"/> 的 Ryn 适配器（R3 边界留在 Presentation；ADR
/// architecture/2026-10-21-window-script-target-port）：本仓唯一的 Ryn 脚本求值适配器——页面注入
/// （横幅/顶栏）、失败面页内恢复面、网页视图导航端口与页面健康探针的求值都经它，
/// 故「怎么对当前窗口求值」只有这一条路径。访问器以提供者惰性供给（装配期 Build 前创建）；
/// 求值点可能早于原生窗口就绪（顶栏注入在主循环之前触发），未就绪由各消费方自行处置（注入路按节拍重试，
/// 健康探针记未知并续跑）。
/// 两类未就绪都收敛为 <see cref="InvalidOperationException"/>：提供者缺位（Build 之前）
/// 与 Ryn <c>Current</c> 未就绪（Ryn 本就以此型表达）——与重试单点的「未就绪」判据同源。
/// </summary>
internal sealed class WindowScriptTarget : IWindowScriptTarget
{
    private readonly Func<CurrentWindowAccessor?> _accessor;

    /// <summary>创建适配器。</summary>
    /// <param name="accessor">当前窗口访问器提供者（Build 后才有值；每次求值时点求值）。</param>
    public WindowScriptTarget(Func<CurrentWindowAccessor?> accessor) => _accessor = accessor;

    /// <inheritdoc />
    public Task<string> EvaluateAsync(string script, CancellationToken ct)
    {
        CurrentWindowAccessor accessor = _accessor()
            ?? throw new InvalidOperationException("窗口访问器尚未就绪（Build 之前无法对页面求值）。");
        return accessor.Current.EvaluateJavaScriptAsync(script, ct).AsTask();
    }
}
