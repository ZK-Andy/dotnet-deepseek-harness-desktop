using System.Reflection;

namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>当前应用版本（单一来源：csproj 的 <c>&lt;Version&gt;</c>，发布时由 tag 覆盖）。</summary>
public static class AppVersion
{
    /// <summary>读取入口程序集的 InformationalVersion；缺失时返回 <c>0.0.0</c>（视为永不满足升级比较）。</summary>
    public static string Current()
    {
        string? informational = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return "0.0.0";
        }

        // InformationalVersion 可能带 SourceRevisionId 后缀（+sha），取主版本段
        int plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
