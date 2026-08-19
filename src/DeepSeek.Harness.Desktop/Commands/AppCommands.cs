using DeepSeek.Harness.Desktop.Services;
using Ryn.Ipc;

namespace DeepSeek.Harness.Desktop.Commands;

/// <summary>Ryn IPC 命令（前端通过 <c>window.__ryn.invoke('app.*')</c> 调用）。</summary>
public static class AppCommands
{
    /// <summary>返回 C# 端生成的问候语（Hello 里程碑的主命令）。</summary>
    [RynCommand("app.hello")]
    public static string Hello(string? name) => GreetingService.Hello(name);
}
