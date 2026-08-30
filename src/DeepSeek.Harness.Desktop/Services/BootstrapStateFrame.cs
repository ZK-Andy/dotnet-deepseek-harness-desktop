namespace DeepSeek.Harness.Desktop.Services;

/// <summary>引导进度帧（wwwroot 引导页监听 <c>dsh-desktop-bootstrap</c> CustomEvent 渲染）。</summary>
internal sealed record BootstrapStateFrame(string Step, string Message, bool Failed);
