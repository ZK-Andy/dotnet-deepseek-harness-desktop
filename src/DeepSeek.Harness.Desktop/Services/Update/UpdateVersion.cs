namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>
/// 版本号逐段比较（纯函数，可单测）。接受可选 <c>v</c> 前缀与预发布后缀（如 <c>v0.1.21-rc.1</c>）：
/// 后缀不参与大小比较，仅数字段逐段比（缺段补 0），全部相等视为同版本。
/// </summary>
public static class UpdateVersion
{
    /// <summary>比较两个版本；返回负数/零/正数表示 a 小于/等于/大于 b。</summary>
    /// <remarks>任一侧无法解析出任何数字段时抛 <see cref="ArgumentException"/>（fail loud，调用方转为 Error 态）。</remarks>
    public static int Compare(string a, string b)
    {
        var left = ParseSegments(a);
        var right = ParseSegments(b);
        var len = Math.Max(left.Length, right.Length);
        for (var i = 0; i < len; i++)
        {
            var l = i < left.Length ? left[i] : 0;
            var r = i < right.Length ? right[i] : 0;
            if (l != r)
            {
                return l.CompareTo(r);
            }
        }

        return 0;
    }

    /// <summary>提取版本中的数字段：<c>v0.1.21-rc.1</c> → <c>[0,1,21]</c>（<c>-rc.1</c> 属预发布标识，截断丢弃）。</summary>
    private static int[] ParseSegments(string version)
    {
        var core = version.Trim().TrimStart('v', 'V');
        var dash = core.IndexOf('-');
        if (dash >= 0)
        {
            core = core[..dash];
        }

        var parts = core.Split('.');
        if (parts.Length == 0 || parts.All(p => !int.TryParse(p, out _)))
        {
            throw new ArgumentException($"无法解析版本号：{version}");
        }

        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            _ = int.TryParse(parts[i], out numbers[i]);
        }

        return numbers;
    }
}
