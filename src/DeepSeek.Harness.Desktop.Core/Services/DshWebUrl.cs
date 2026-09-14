namespace DeepSeek.Harness.Desktop.Core;

/// <summary>
/// dsh web 端点（branded 值类型，ADR composition-root-value-flow-pipeline 批次 2）：运行时 URL 跨
/// R3 边界（宿主启动/首启引导 → 壳侧导航）的传输形态。裸 <see cref="Uri"/> 只在实现内部流通，
/// 端口与阶段产出用本类型，避免同一 URL 以裸形态在边界两侧各自议定。
/// </summary>
public readonly record struct DshWebUrl
{
    private readonly Uri? _value;

    private DshWebUrl(Uri value) => _value = value;

    /// <summary>底层 URL；解包点限于导航 API 与显示。</summary>
    /// <exception cref="InvalidOperationException">空实例（<c>default</c>/无参构造）访问——须经
    /// <see cref="From"/>/<see cref="FromNullable"/> 构造。</exception>
    public Uri Value => _value ?? throw new InvalidOperationException(
        "DshWebUrl 空实例：default 构造无 URL，须经 From/FromNullable 构造");

    /// <summary>从已解析出的运行时 URL 构造。</summary>
    /// <param name="url">非空 URL。</param>
    /// <returns>branded 端点。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="url"/> 为 null。</exception>
    public static DshWebUrl From(Uri url) => new(url ?? throw new ArgumentNullException(nameof(url)));

    /// <summary>可空形态构造（宿主未在时限内给出 URL 时保持 null）。</summary>
    /// <param name="url">可空 URL。</param>
    /// <returns>branded 端点；<paramref name="url"/> 为 null 时为 null。</returns>
    public static DshWebUrl? FromNullable(Uri? url) => url is null ? null : new DshWebUrl(url);

    /// <summary>origin（scheme + authority）：IPC 受信 origin 与同站导航靶点用。</summary>
    public string Authority => Value.GetLeftPart(UriPartial.Authority);

    /// <summary>origin 根（含尾斜杠）：WebKitGTK 跨 scheme 两跳导航的第一跳靶点。</summary>
    public Uri AuthorityRoot => new(Authority + "/");

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}
