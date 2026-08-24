# Agent Note: companion-invoke-frame-contract

Status: implemented

## Problem

v0.3.0 Linux rpm 实机验收中，companion 两个新区块同时失效：①「开机自启」开关渲染后按钮永久禁用（点击无反应）；②「诊断导出」宿主侧两次成功落包（host.log 与 `~/文档` zip 实证）页面却显示失败。根因同一：Ryn 桥接 `ryn.invoke` 对非空响应 resolve 的是 **JSON.parse 后的值**（对象；RynWebView 桥接源码与官方示例 `invoke(...) as SystemInfo` 双实证），而两区块对响应直接二次 `JSON.parse(res)`——对象被 String 化为 `"[object Object]"` 抛异常被吞，自启区块的 `enabled` 因此永远停在 null（按钮 disabled）、导出区块落入错误分支。旧代码 `parseState` 在 v0.2.x 实机调试时已学过该契约，但教训停留在函数局部、未提炼成约定，后续区块重蹈。dev 门禁下命令路由不存在，invoke 走 reject 分支，「优雅降级」在沙箱被误当验证通过——成功分支直到真机才首次执行。

## Decision

1. client.js 以统一归一化助手 `parseFrame(raw)`（对象直通 / 字符串尝试解析 / 失败返 null）收口全部 invoke 响应：更新状态、自启 getState/set、诊断导出四处调用点全走它，注释钉死桥接契约并禁止直接 JSON.parse。
2. 插件 version bump **0.0.7→0.0.8**，随壳下次发版经既有版本感知升级自动替换用户 profile 内副本；本约定「改插件必须 bump」继续有效。
3. 回归载体 = 实机验收清单（沙箱无 Ryn 桥接真环境）：开关显示真实状态且切换生效（`~/.config/autostart` 条目随动）；导出点击后显示「已保存至：<路径>」。

## Alternatives considered

- **改 Ryn 桥接回传原始字符串**：落败——官方示例与生态按解析后的值消费，改桥接破坏面覆盖全部插件调用方，还需上游协调发版；客户端适配是零依赖单侧修。
- **只修两处报错点、不立统一助手**：落败——正是「局部修不提炼」导致 parseState 的教训没有传到后续区块；统一入口 + 注释契约才防第三次重蹈。
- **响应帧改为自定义包裹协议（如 {ok,data}）**：落败——宿主路由帧形态已有多处消费方（更新路由同款 error 帧），换协议是全局破坏性改动，收益为零。

## Consequences

- 自启/诊断/更新三面对字符串与对象两种形态都健壮，兼容未来桥接行为微调。
- 插件无 JS 测试基建（手写无打包器），契约正确性由注释 + 本笔记 + 真机清单共同承载；若日后桥接契约再变，先改此处的归一化层。
- dev 运行时验证盲区留档：凡「路由不存在即降级」的功能，沙箱只能验降级分支，成功分支必须真机或显式 force 开关（`DSH_DESKTOP_UPDATE_FORCE=1` 同理思路）过一遍。

## Related

- [shell 首启加固](2026-08-24-shell-firstboot-hardening.md)：同轮实机验收批次，托盘顺序/横幅门控/导出回退三项壳侧修复。
- [companion 更新设置页](../feature/2026-08-22-companion-update-settings-section.md)与[开机自启](../architecture/2026-08-24-shell-convenience-autostart-ready-notify.md)：两个失效区块的落地笔记。
- [桌面伴生插件](../process/2026-08-21-desktop-shell-companion-plugin.md)：插件工程形态与「必须 bump version」约定的出处。
