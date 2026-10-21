using DeepSeek.Harness.Desktop.Core.Recovery;

namespace DeepSeek.Harness.Desktop.Tests.Fakes;

/// <summary>假原生失败模态端口（<see cref="IFailureDialog"/> 替身）：记录标题/正文，按预置答案回 true/false，
/// 可预置异常模拟通道故障（ADR native-failure-surface 的「false ≠ 破坏性许可」契约由此钉住；
/// 故障钩子的用例形状：模态抛异常时异常向调用方传播，但闸门与窗口都不动——通道故障不等于用户授权退出）。</summary>
internal sealed class FakeFailureDialog : IFailureDialog
{
    /// <summary>本机是否具备原生通道。</summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>模态返回值（true = 用户选「退出应用」）。</summary>
    public bool Answer { get; set; }

    /// <summary>非 null 即让模态抛该异常（模拟通道故障）。</summary>
    public Exception? Failure { get; set; }

    /// <summary>每次弹出的（标题, 正文）。</summary>
    public List<(string Title, string Message)> Shown { get; } = [];

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken ct)
    {
        Shown.Add((title, message));
        return Failure is null ? Task.FromResult(Answer) : Task.FromException<bool>(Failure);
    }
}
