using System.Text.Json.Serialization;

namespace DeepSeek.Harness.Desktop.Services;

/// <summary>插件引导页推送帧（wwwroot 监听 <c>dsh-desktop-preinstall</c> CustomEvent 渲染）。</summary>
/// <param name="Kind">decision | installing | log | done——页面按其分支渲染。</param>
/// <param name="Plugins">decision 时待装可选插件名列表（引导页渲染 chip）。</param>
/// <param name="Plugin">installing 时当前安装的插件名。</param>
/// <param name="Line">log 时一行安装输出。</param>
/// <param name="Action">done 时用户动作（install | skip）。</param>
/// <param name="Ok">done 时安装成功与否。</param>
/// <param name="Message">done / 失败时人读消息。</param>
internal sealed record PreinstallFrame(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Plugins = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Plugin = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Line = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Action = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Ok = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null);
