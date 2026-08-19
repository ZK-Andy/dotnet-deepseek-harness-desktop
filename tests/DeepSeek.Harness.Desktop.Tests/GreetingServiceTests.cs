using DeepSeek.Harness.Desktop.Services;
using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>GreetingService 行为回归：边界（空/空白/null/修剪）与正常路径。</summary>
public class GreetingServiceTests
{
    [Fact]
    public void Hello_ReturnsGreetingWithTrimmedName()
    {
        Assert.Equal("Hello, ZK! — from DeepSeek.Harness.Desktop (C#)", GreetingService.Hello("  ZK  "));
    }

    [Fact]
    public void Hello_WhitespaceName_FallsBackToWorld()
    {
        Assert.Equal("Hello, World! — from DeepSeek.Harness.Desktop (C#)", GreetingService.Hello("   "));
    }

    [Fact]
    public void Hello_NullName_FallsBackToWorld()
    {
        Assert.Equal("Hello, World! — from DeepSeek.Harness.Desktop (C#)", GreetingService.Hello(null));
    }
}
