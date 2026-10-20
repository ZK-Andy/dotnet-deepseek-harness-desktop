using System.Text;
using DeepSeek.Harness.Desktop.Core.Ipc;

namespace DeepSeek.Harness.Desktop.Core.Tests.Ipc;

/// <summary>命令参数帧读取单点（跨包契约层）：开关帧 <c>enabled</c> 的取值规则。</summary>
public class CommandFrameTests
{
    /// <summary>验证 <c>enabled</c> 为 JSON true 时读出 true。</summary>
    [Fact]
    public void ReadEnabled_TrueJson_ReadsTrue() =>
        Assert.True(CommandFrame.ReadEnabled(Frame("""{"enabled":true}""")));

    /// <summary>验证 <c>enabled</c> 为 false 时读出 false。</summary>
    [Fact]
    public void ReadEnabled_FalseJson_ReadsFalse() =>
        Assert.False(CommandFrame.ReadEnabled(Frame("""{"enabled":false}""")));

    /// <summary>验证字段缺失时按 false（缺字段 ≠ 开）。</summary>
    [Fact]
    public void ReadEnabled_MissingField_ReadsFalse() =>
        Assert.False(CommandFrame.ReadEnabled(Frame("""{"other":1}""")));

    /// <summary>验证非布尔取值（字符串/数字）按 false：不把「非 true」误判为开。</summary>
    [Fact]
    public void ReadEnabled_NonBooleanValue_ReadsFalse()
    {
        Assert.False(CommandFrame.ReadEnabled(Frame("""{"enabled":"true"}""")));
        Assert.False(CommandFrame.ReadEnabled(Frame("""{"enabled":1}""")));
    }

    private static ReadOnlyMemory<byte> Frame(string json) => Encoding.UTF8.GetBytes(json);
}
