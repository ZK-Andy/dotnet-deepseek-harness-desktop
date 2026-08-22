namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>
/// 版本号逐段比较（纯函数，可单测）。接受可选 <c>v</c> 前缀与预发布后缀（如 <c>v0.1.21-rc.1</c>）：
/// 后缀不参与大小比较，仅数字段逐段比（缺段补 0），全部相等视为同版本。
/// </summary>
public static class UpdateVersion
{
    /// <summary>比较两个版本；返回负数/零/正数表示 a 小于/等于/大于 b。</summary>
    /// <remarks>任一段无法解析为数字时抛 <see cref="ArgumentException"/>（fail loud，调用方转为 Error 态或清除残留记录）。</remarks>
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
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            // 混合形态（如 "0.a.3"）静默补 0 会掩盖脏数据，逐段校验、任一失败即抛
            if (!int.TryParse(parts[i], out numbers[i]))
            {
                throw new ArgumentException($"无法解析版本号：{version}");
            }
        }

        return numbers;
    }
}
