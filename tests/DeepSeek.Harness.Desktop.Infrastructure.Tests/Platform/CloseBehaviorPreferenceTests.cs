
namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Platform;

/// <summary>「关闭时最小化到托盘」偏好：缺省 true（历史行为）、损坏回退、读写往返。</summary>
public class CloseBehaviorPreferenceTests
{
    private static string TempPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ddc-close-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, CloseBehaviorPreference.FileName);
    }

    /// <summary>验证无持久化文件时 HideOnClose 缺省回退为 true，保持历史行为（存量升级零感知）。</summary>
    [Fact]
    public void MissingFile_FallsBackToTrue()
    {
        // 存量升级零感知：无持久化文件时保持历史行为（托盘可用即隐藏）
        Assert.True(new CloseBehaviorPreference(TempPath()).HideOnClose);
    }

    /// <summary>验证持久化文件损坏（非 JSON）时 HideOnClose 回退为 true 而非抛错。</summary>
    [Fact]
    public void CorruptFile_FallsBackToTrue()
    {
        string path = TempPath();
        File.WriteAllText(path, "{not-json");
        Assert.True(new CloseBehaviorPreference(path).HideOnClose);
    }

    /// <summary>验证 Set(false) 落盘 hideToTrayOnClose:false，新建实例重载后仍保持关闭。</summary>
    [Fact]
    public void SetFalse_PersistsAndSurvivesReload()
    {
        string path = TempPath();
        var pref = new CloseBehaviorPreference(path);
        pref.Set(false);
        Assert.False(pref.HideOnClose);
        Assert.False(new CloseBehaviorPreference(path).HideOnClose);
        Assert.Contains("\"hideToTrayOnClose\":false", File.ReadAllText(path));
    }

    /// <summary>验证 Set(true) 在文件中显式写出 hideToTrayOnClose:true，不依赖缺省值。</summary>
    [Fact]
    public void SetTrue_WritesExplicitTrue()
    {
        string path = TempPath();
        new CloseBehaviorPreference(path).Set(true);
        Assert.Contains("\"hideToTrayOnClose\":true", File.ReadAllText(path));
    }
}
