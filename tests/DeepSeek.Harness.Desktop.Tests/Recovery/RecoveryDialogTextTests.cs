namespace DeepSeek.Harness.Desktop.Tests.Recovery;

/// <summary>失败面原生对话框正文（ADR native-failure-surface）：原因 + 尾部 + 诊断包路径 + 动作说明；展示上限裁剪、英文不留中文、空尾部与导出失败退化。</summary>
public class RecoveryDialogTextTests
{
    /// <summary>正文载原因（开头）+ 尾部小节头与逐行尾部 + 导出路径 + 动作说明；英文分支不留中文。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_CarriesReasonTailPathAndActions(bool english)
    {
        string reason = UiCopy.ReasonRuntimeCrashed(english);
        string text = RecoveryDialogText.Build(reason, new[] { "line-1", "line-2" }, "/tmp/diag.zip", english);

        Assert.StartsWith(reason, text, StringComparison.Ordinal);
        Assert.Contains(UiCopy.RecoveryDialogTailHeading(english), text);
        Assert.Contains(UiCopy.RecoveryDialogExportPrefix(english) + "/tmp/diag.zip", text);
        Assert.Contains(UiCopy.RecoveryDialogActions(english), text);
        Assert.Contains("line-1", text);
        Assert.Contains("line-2", text);
        if (english)
        {
            // 模态文本无渲染层：英文分支混入中文即缺译
            Assert.DoesNotContain("诊断包", text);
            Assert.DoesNotContain("退出应用", text);
        }
    }

    /// <summary>空尾部不出现尾部小节头、导出失败取失败文案；展示上限＝末 8 行 + 单行 160 字符裁剪（原生模态不适合长文本）。</summary>
    [Fact]
    public void Build_DegradesAndClips()
    {
        string noTail = RecoveryDialogText.Build("原因", Array.Empty<string>(), "/tmp/diag.zip", english: false);
        Assert.DoesNotContain(UiCopy.RecoveryDialogTailHeading(english: false), noTail);
        string noExport = RecoveryDialogText.Build("原因", Array.Empty<string>(), string.Empty, english: false);
        Assert.Contains(UiCopy.RecoveryDialogExportFailed(english: false), noExport);
        Assert.DoesNotContain(UiCopy.RecoveryDialogExportPrefix(english: false), noExport);

        string[] tail = Enumerable.Range(1, 20).Select(i => $"line-{i}").ToArray();
        string clippedTail = RecoveryDialogText.Build("原因", tail, string.Empty, english: false);
        Assert.DoesNotContain("line-12", clippedTail);
        Assert.Contains("line-20", clippedTail);

        string longLine = new('x', 500);
        string clippedLine = RecoveryDialogText.Build("原因", new[] { longLine }, string.Empty, english: false);
        Assert.DoesNotContain(longLine, clippedLine);
        Assert.Contains(new string('x', 160), clippedLine);
    }
}
