using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeek.Harness.Desktop.Services.Update;

/// <summary>自更新状态机状态（对齐 opencode updater-controller：ready 之外全部静默）。</summary>
public enum UpdateStatus
{
    /// <summary>初始态/尚未检查。</summary>
    Idle,

    /// <summary>正在抓 feed 比对版本。</summary>
    Checking,

    /// <summary>发现新版本，正在下载安装包。</summary>
    Downloading,

    /// <summary>安装包就绪且校验通过——唯一对外可见的状态。</summary>
    Ready,

    /// <summary>用户点击后正在执行安装器并重启壳。</summary>
    Installing,

    /// <summary>已是最新版。</summary>
    UpToDate,

    /// <summary>检查/下载失败；消息进日志，UI 静默。</summary>
    Error,
}

/// <summary>状态机当前快照。</summary>
/// <param name="Status">当前状态。</param>
/// <param name="Version">ready/installing/download 态携带的目标版本号。</param>
/// <param name="Message">error 态的失败原因（日志与设置页错误行展示）。</param>
/// <param name="Current">当前壳版本（Transition 统一补齐；供更新页显示「当前版本/已是最新」）。</param>
public sealed record UpdateState(UpdateStatus Status, string? Version = null, string? Message = null, string? Current = null)
{
    /// <summary>序列化为页面推送用的紧凑 JSON（经 <see cref="AppJsonContext"/> 源生成，AOT 安全）。
    /// 三个动态字段统一转义，不依赖「上游已把版本号校验为数字段」的隐式约定。</summary>
    public string ToJson() =>
        JsonSerializer.Serialize(
            new UpdateStateFrame(Status.ToString().ToLowerInvariant(), Version, Message, Current),
            AppJsonContext.Default.UpdateStateFrame);}

/// <summary><see cref="UpdateState"/> 的页面推送帧（键序 = 声明序，companion 消费契约）：
/// <c>status</c> 由状态枚举手动小写——全小写 <c>uptodate</c> 是插件侧 switch 契约，不可改用
/// 枚举命名策略；<c>version</c> 恒在场（缺省 null 字面量），<c>message</c>/<c>current</c> 仅非空出现。</summary>
/// <param name="Status">已小写的状态串。</param>
/// <param name="Version">目标版本号；无则 null。</param>
/// <param name="Message">error 态失败原因；null 时整键省略。</param>
/// <param name="Current">当前壳版本；null 时整键省略。</param>
internal sealed record UpdateStateFrame(
    string Status,
    string? Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Current);
