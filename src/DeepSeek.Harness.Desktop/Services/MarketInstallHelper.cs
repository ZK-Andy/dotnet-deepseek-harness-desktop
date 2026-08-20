using System.Text.Json;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>市场后台安装的纯逻辑（可单测）：检测、迁移、workspace 修正、spec 解析、bundles 补写。</summary>
public static class MarketInstallHelper
{
    /// <summary>精确检测市场是否已就位：<c>dependencies.dshmarket</c> 且 <c>dsh.profile.bundles</c> 含 <c>dshmarket</c>。</summary>
    public static bool IsMarketInstalled(string profilePkg)
    {
        try
        {
            if (!File.Exists(profilePkg))
            {
                return false;
            }

            var json = File.ReadAllText(profilePkg);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("dependencies", out var deps))
            {
                return false;
            }

            if (!deps.TryGetProperty("dshmarket", out _))
            {
                return false;
            }

            if (!root.TryGetProperty("dsh", out var dsh) ||
                !dsh.TryGetProperty("profile", out var profile) ||
                !profile.TryGetProperty("bundles", out var bundles) ||
                bundles.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var b in bundles.EnumerateArray())
            {
                if (b.GetString() == "dshmarket")
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>清理 <c>0.1.10</c> 误写入的 <c>dependencies.app=file:...dshmarket.tgz</c>。</summary>
    public static async Task CleanupBogusAppDependencyAsync(string profilePkg)
    {
        try
        {
            if (!File.Exists(profilePkg))
            {
                return;
            }

            var json = await File.ReadAllTextAsync(profilePkg);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("dependencies", out var deps) ||
                !deps.TryGetProperty("app", out var appVal))
            {
                return;
            }

            var appSpec = appVal.GetString() ?? string.Empty;
            if (!appSpec.Contains("dshmarket.tgz", StringComparison.Ordinal))
            {
                return;
            }

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == "dependencies")
                    {
                        writer.WritePropertyName("dependencies");
                        writer.WriteStartObject();
                        foreach (var dep in prop.Value.EnumerateObject())
                        {
                            if (dep.Name == "app" && dep.Value.GetString()?.Contains("dshmarket.tgz") == true)
                            {
                                continue;
                            }

                            dep.WriteTo(writer);
                        }

                        writer.WriteEndObject();
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            var newJson = System.Text.Encoding.UTF8.GetString(ms.ToArray()) + "\n";
            await File.WriteAllTextAsync(profilePkg, newJson);
        }
        catch
        {
        }
    }

    /// <summary>确保 <c>pnpm-workspace.yaml</c> 的 <c>allowBuilds</c> 放行 6 项原生构建。</summary>
    public static void EnsureWorkspaceAllowBuilds(string workspacePath)
    {
        try
        {
            if (!File.Exists(workspacePath))
            {
                return;
            }

            var text = File.ReadAllText(workspacePath);
            var original = text;
            if (text.Contains("set this to true or false"))
            {
                text = text.Replace(": set this to true or false", ": true");
            }

            if (!text.Contains("esbuild"))
            {
                if (text.Contains("allowBuilds:"))
                {
                    text = text.Replace("allowBuilds:", "allowBuilds:\n  esbuild: true");
                }
                else
                {
                    text = text.TrimEnd() + "\nallowBuilds:\n  esbuild: true\n";
                }
            }
            else if (text.Contains("esbuild: set this"))
            {
                text = text.Replace("esbuild: set this to true or false", "esbuild: true");
            }

            var required = new[] { "@deepseek-ai/dsh-subprocess-local", "@google/genai", "koffi", "node-pty", "protobufjs" };
            foreach (var pkg in required)
            {
                if (!text.Contains(pkg))
                {
                    if (text.Contains("allowBuilds:"))
                    {
                        text = text.Replace("allowBuilds:", $"allowBuilds:\n  '{pkg}': true");
                    }
                }
            }

            if (text != original)
            {
                File.WriteAllText(workspacePath, text);
            }
        }
        catch
        {
        }
    }

    /// <summary>解析市场安装的 <c>spec</c>：优先校验过的 <c>tgz &gt;10K</c>，否则目录或 registry。</summary>
    public static string ResolveMarketSpec(string runtimeDir)
    {
        var tgz = Path.Combine(runtimeDir, "dshmarket.tgz");
        if (File.Exists(tgz))
        {
            try
            {
                var fi = new FileInfo(tgz);
                if (fi.Length > 10 * 1024)
                {
                    return tgz;
                }
            }
            catch
            {
            }
        }

        var dir = Path.Combine(runtimeDir, "node_modules", "dshmarket");
        if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "package.json")))
        {
            return dir;
        }

        return "dshmarket@1.15.0";
    }

    /// <summary>确保 <c>dsh.profile.bundles</c> 含 <c>dshmarket</c>，缺则追加并写回。</summary>
    /// <returns>是否发生写回。</returns>
    public static async Task<bool> EnsureBundlesContainsMarketAsync(string profilePkg)
    {
        try
        {
            if (!File.Exists(profilePkg))
            {
                return false;
            }

            var json = await File.ReadAllTextAsync(profilePkg);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("dsh", out var dsh) ||
                !dsh.TryGetProperty("profile", out var profile) ||
                !profile.TryGetProperty("bundles", out var bundles) ||
                bundles.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var b in bundles.EnumerateArray())
            {
                if (b.GetString() == "dshmarket")
                {
                    return false;
                }
            }

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == "dsh")
                    {
                        writer.WritePropertyName("dsh");
                        writer.WriteStartObject();
                        foreach (var dshProp in prop.Value.EnumerateObject())
                        {
                            if (dshProp.Name == "profile")
                            {
                                writer.WritePropertyName("profile");
                                writer.WriteStartObject();
                                foreach (var profProp in dshProp.Value.EnumerateObject())
                                {
                                    if (profProp.Name == "bundles")
                                    {
                                        writer.WritePropertyName("bundles");
                                        writer.WriteStartArray();
                                        foreach (var b in profProp.Value.EnumerateArray())
                                        {
                                            b.WriteTo(writer);
                                        }

                                        writer.WriteStringValue("dshmarket");
                                        writer.WriteEndArray();
                                    }
                                    else
                                    {
                                        profProp.WriteTo(writer);
                                    }
                                }

                                if (!dshProp.Value.TryGetProperty("bundles", out _))
                                {
                                    writer.WritePropertyName("bundles");
                                    writer.WriteStartArray();
                                    writer.WriteStringValue("dshmarket");
                                    writer.WriteEndArray();
                                }

                                writer.WriteEndObject();
                            }
                            else
                            {
                                dshProp.WriteTo(writer);
                            }
                        }

                        writer.WriteEndObject();
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            var newJson = System.Text.Encoding.UTF8.GetString(ms.ToArray()) + "\n";
            await File.WriteAllTextAsync(profilePkg, newJson);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
