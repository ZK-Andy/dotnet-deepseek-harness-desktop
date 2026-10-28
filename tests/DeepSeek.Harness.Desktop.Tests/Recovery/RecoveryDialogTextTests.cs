namespace DeepSeek.Harness.Desktop.Tests.Recovery;

/// <summary>失败面原生对话框正文（ADR native-failure-surface）：原因 + 尾部 + 诊断包路径 + 动作说明；展示上限裁剪、英文不留中文、空尾部与导出失败退化。</summary>
public class RecoveryDialogTextTests
{
    /// <summary>正文载原因（开头）+ 尾部小节头与逐行尾部 + 导出路径 + 动作说明；英文分支不留中文。</summary>
    [Theory]
    [InlineData(UiLanguage.Zh)]
    [InlineData(UiLanguage.En)]
    public void Build_CarriesReasonTailPathAndActions(UiLanguage lang)
    {
        string reason = UiCopy.ReasonRuntimeCrashed(lang);
        string text = RecoveryDialogText.Build(reason, new[] { "line-1", "line-2" }, "/tmp/diag.zip", lang);

        Assert.StartsWith(reason, text, StringComparison.Ordinal);
        Assert.Contains(UiCopy.RecoveryDialogTailHeading(lang), text);
        Assert.Contains(UiCopy.RecoveryDialogExportPrefix(lang) + "/tmp/diag.zip", text);
        Assert.Contains(UiCopy.RecoveryDialogActions(lang), text);
        Assert.Contains("line-1", text);
        Assert.Contains("line-2", text);
        if (lang != UiLanguage.Zh)
        {
            // 模态文本无渲染层：英文分支混入中文即缺译
            Assert.DoesNotContain("诊断包", text);
            Assert.DoesNotContain("退出应用", text);
        }
    }

    /// <summary>不涉及诊断导出（exportPath=null）时整节省略——不能拿「导出失败」冒充「未尝试」；
    /// 动作行可换（启动失败没有自动重启，崩溃恢复口径不适用）。</summary>
    [Fact]
    public void Build_WithoutExportAndCustomActions_OmitsExportAndUsesGivenActions()
    {
        string text = RecoveryDialogText.Build(
            UiCopy.StartupFailureReason(lang: UiLanguage.Zh),
            Array.Empty<string>(),
            exportPath: null,
            lang: UiLanguage.Zh,
            actions: UiCopy.StartupFailureActions(lang: UiLanguage.Zh));

        Assert.DoesNotContain(UiCopy.RecoveryDialogExportFailed(lang: UiLanguage.Zh), text);
        Assert.DoesNotContain(UiCopy.RecoveryDialogExportPrefix(lang: UiLanguage.Zh), text);
        Assert.Contains(UiCopy.StartupFailureActions(lang: UiLanguage.Zh), text);
        Assert.DoesNotContain(UiCopy.RecoveryDialogActions(lang: UiLanguage.Zh), text);
    }

    /// <summary>空尾部不出现尾部小节头、导出失败取失败文案；展示上限＝末 8 行 + 单行 160 字符裁剪
    /// + 重复行折叠 + 尾部节 600 字符预算装箱（原生模态无滚动，超长会把按钮顶出屏幕）。</summary>
    [Fact]
    public void Build_DegradesAndClips()
    {
        string noTail = RecoveryDialogText.Build("原因", Array.Empty<string>(), "/tmp/diag.zip", lang: UiLanguage.Zh);
        Assert.DoesNotContain(UiCopy.RecoveryDialogTailHeading(lang: UiLanguage.Zh), noTail);
        string noExport = RecoveryDialogText.Build("原因", Array.Empty<string>(), string.Empty, lang: UiLanguage.Zh);
        Assert.Contains(UiCopy.RecoveryDialogExportFailed(lang: UiLanguage.Zh), noExport);
        Assert.DoesNotContain(UiCopy.RecoveryDialogExportPrefix(lang: UiLanguage.Zh), noExport);

        string[] tail = Enumerable.Range(1, 20).Select(i => $"line-{i}").ToArray();
        string clippedTail = RecoveryDialogText.Build("原因", tail, string.Empty, lang: UiLanguage.Zh);
        Assert.DoesNotContain("line-12", clippedTail);
        Assert.Contains("line-20", clippedTail);

        string longLine = new('x', 500);
        string clippedLine = RecoveryDialogText.Build("原因", new[] { longLine }, string.Empty, lang: UiLanguage.Zh);
        Assert.DoesNotContain(longLine, clippedLine);
        Assert.Contains(new string('x', 160), clippedLine);
    }

    /// <summary>重复尾部行折叠（保首次出现序，记语言中立的 (×N) 后缀；单次行保持原样）。</summary>
    [Fact]
    public void Build_CollapsesRepeatedTailLines()
    {
        string text = RecoveryDialogText.Build(
            "原因", new[] { "a", "b", "a", "a", "c", "b" }, "/tmp/diag.zip", lang: UiLanguage.Zh);

        Assert.Contains("a (×3)", text);
        Assert.Contains("b (×2)", text);
        Assert.Contains("\nc\n", text);
        Assert.DoesNotContain("(×1)", text);
    }

    /// <summary>尾部节预算装箱：8 行满幅长行只装下最新若干行（旧行先退场；原因/导出/动作恒在，故本用例总额仍 ≤ 600）。</summary>
    [Fact]
    public void Build_FitsTailWithinBudgetNewestFirst()
    {
        string[] tail = Enumerable.Range(1, 8).Select(i => $"tail-{i}-" + new string('z', 150)).ToArray();
        string text = RecoveryDialogText.Build("原因", tail, "/tmp/diag.zip", lang: UiLanguage.Zh);

        Assert.True(text.Length <= 600);
        Assert.Contains("tail-8-", text);
        Assert.DoesNotContain("tail-1-", text);
        Assert.Contains(UiCopy.RecoveryDialogTailHeading(lang: UiLanguage.Zh), text);
    }
}
