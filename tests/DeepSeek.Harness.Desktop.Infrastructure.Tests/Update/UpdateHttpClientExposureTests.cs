using System.Reflection;
using System.Runtime.CompilerServices;

namespace DeepSeek.Harness.Desktop.Infrastructure.Tests.Update;

/// <summary>自更新链路的 HTTP 出口唯一性回归（ADR architecture/2026-10-20-update-stack-adapter-split）：
/// Infrastructure 的 Update 命名空间内，<b>静态</b>的客户端来源恰一个——<c>UpdateHttpClient.Shared</c>。
/// 第二个静态成员即第二个连接池。
/// <para><b>判据走已编译程序集的反射枚举，不做源文本扫描</b>：源锚追不完形态（注释、字符串、别名、
/// 泛型、数组、实例作用域……每轮都要补漏），而这条不变量在类型系统里可枚举——静态成员（字段／属性／
/// 方法，任意可见性）逐个看其类型是否<b>含</b> <see cref="HttpClient"/>：本身、数组元素、任意深度的
/// 泛型实参（<c>Lazy&lt;HttpClient&gt;</c>／<c>Lazy&lt;Task&lt;HttpClient&gt;&gt;</c>／
/// <c>Dictionary&lt;string, HttpClient&gt;</c>）。
/// </para>
/// <para><b>未钉住的部分（此处是判据面的唯一家，ADR 只留指针）</b>：
/// ① <b>实例持有</b>形态不在判据面——worker 以实例字段持有共享客户端是本目录的既有合法形状
/// （<c>InstallerDownloader</c>／<c>ReleaseMetaClient</c>），查实例面会与合法形态冲突；故「第二个客户端
/// 以实例字段形式出现」今天不被这条判据拦。
/// ② <b>方法体内、以及非客户端类型的静态字段初始化器里的 <c>new HttpClient(...)</c></b> 反射看不见——
/// 靠「<c>UpdateHttpClient</c> 为 internal + 无工厂方法 + 本条判据 + 评审纪律」共同兜住。
/// ③ 类型加载失败的程序集只报已加载类型与失败类型名（不抛 <see cref="ReflectionTypeLoadException"/>）。
/// </para>
/// </summary>
public class UpdateHttpClientExposureTests
{
    /// <summary>静态成员的反射枚举标志（任意可见性；实例成员不在判据面）。</summary>
    private const BindingFlags StaticMembers =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Update 命名空间（客户端来源的判定面）。</summary>
    private const string UpdateNamespace = "DeepSeek.Harness.Desktop.Infrastructure.Update";

    /// <summary>Update 命名空间内的静态客户端来源恰一个，且是共享实例 <c>UpdateHttpClient.Shared</c>。
    /// 断言只绑「所在类型 + 成员名」，不绑成员形态（属性／字段／方法等价——形态是实现细节）。</summary>
    [Fact]
    public void UpdateChain_HasExactlyOneStaticHttpClientSource()
    {
        List<string> sources = [];
        List<string> loadFailures = [];

        foreach (Type type in TypesOfUpdateNamespace(typeof(UpdateHttpClient).Assembly, loadFailures))
        {
            foreach (FieldInfo field in type.GetFields(StaticMembers))
            {
                // 自动属性的后备字段（<Shared>k__BackingField）与属性同源，跳过（否则一个成员被数两次）。
                if (field.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                {
                    continue;
                }

                if (IsClientSource(field.FieldType))
                {
                    sources.Add($"{type.Name}.{field.Name}");
                }
            }

            foreach (PropertyInfo property in type.GetProperties(StaticMembers))
            {
                if (IsClientSource(property.PropertyType))
                {
                    sources.Add($"{type.Name}.{property.Name}");
                }
            }

            foreach (MethodInfo method in type.GetMethods(StaticMembers))
            {
                // 属性存取器（get_/set_）与属性重复计数，跳过（IsSpecialName 覆盖全部存取器形态）。
                if (method.IsSpecialName)
                {
                    continue;
                }

                if (IsClientSource(method.ReturnType))
                {
                    sources.Add($"{type.Name}.{method.Name}");
                }
            }
        }

        Assert.Empty(loadFailures);
        string source = Assert.Single(sources);
        Assert.Equal("UpdateHttpClient.Shared", source);
    }

    /// <summary>枚举 Update 命名空间下的类型（类型加载失败时点名报告，不抛异常栈）。</summary>
    private static IEnumerable<Type> TypesOfUpdateNamespace(Assembly assembly, List<string> loadFailures)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            loadFailures.AddRange(
                ex.LoaderExceptions
                    .Where(e => e is not null)
                    .Select(e => e!.Message ?? e!.GetType().Name));
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }

        foreach (Type type in types)
        {
            if (type.Namespace == UpdateNamespace)
            {
                yield return type;
            }
        }
    }

    /// <summary>客户端来源判据：类型本身是 <see cref="HttpClient"/>、是元素为客户端来源的数组，
    /// 或任一泛型实参是客户端来源（任意深度包装）。</summary>
    private static bool IsClientSource(Type type)
    {
        if (type == typeof(HttpClient))
        {
            return true;
        }

        if (type.IsArray)
        {
            return type.GetElementType() is { } element && IsClientSource(element);
        }

        return type.IsGenericType && type.GetGenericArguments().Any(IsClientSource);
    }
}
