using System.Text.Json;

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
    /// <summary>序列化为页面推送用的紧凑 JSON（手写避免 AOT 反射序列化）。</summary>
    public string ToJson()
    {
        var version = Version is null ? "null" : $"\"{Version}\"";
        var json = $"{{\"status\":\"{Status.ToString().ToLowerInvariant()}\",\"version\":{version}";
        if (Message is not null)
        {
            json += $",\"message\":\"{JsonEncodedText.Encode(Message)}\"";
        }

        if (Current is not null)
        {
            json += $",\"current\":\"{Current}\"";
        }

        return json + "}";
    }
}
