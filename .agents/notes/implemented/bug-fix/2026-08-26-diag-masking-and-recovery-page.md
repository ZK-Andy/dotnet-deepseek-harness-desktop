# Agent Note: diag-masking-and-recovery-page

Status: implemented

## Problem

对照 anywhere-labs/dsh-desktop（2026-08-26）暴露两项差距，且我方历史事故链坐实了价值：

1. **诊断脱敏缺位**：`HostLog.Write` 是 stdout 与 `host.log` 落盘的唯一出口，内容零脱敏——stderr 尾部（上游 dsh 的不可控输出）经监督器原样落盘；而诊断 zip 白名单收录 host.log 原文且是用户主动外发的唯一通道（贴 issue / 发社区），凭据形状内容一旦进入日志即有泄漏通道。
2. **恢复页只有一行转圈**：`RecoveryScript` 覆写后用户只知道「坏了」，不知道为什么、能做什么；崩溃时刻恰是取证与求助需求最强的时刻。

## Decision

1. **`SecretMasker` 纯函数挂 `HostLog.Write` 入口**：分层——URL 敏感查询键（token/api_key/secret/password 等，只遮值保留键名）、引号键值对（JSON/YAML 形态、键名引号可选、键名限定敏感集合）、裸形状兜底（`sk-` 前缀保留前缀便于识别被遮对象；32–64 位十六进制限字符集防误伤普通词）；Cookie/Set-Cookie/Authorization 头整值遮罩**殿后**——它吞掉行内自冒号起的全部剩余内容，必须最后执行。误伤方向刻意偏安全。
2. **恢复页三件套**：`RecoveryPageBuilder.BuildScript(原因, stderr 尾部)` 生成覆写脚本——静态骨架为编译期常量（无外部依赖不漂移），动态数据 JSON 序列化（默认编码器：`<` 与非 ASCII 全 `\u` 转义）后一律 `textContent` 回填，绝不 innerHTML 拼接；「导出诊断包」复用既有 `desktop.diagnostics.export`，「退出应用」走新增 `desktop.recovery.exit`。desktop.* 命令是 Ryn 层 IPC 不依赖 dsh 存活，零上游改动。
3. **退出顺序契约**：`RecoveryCommandRouter` 先 `CloseGate.ApproveExit()` 再 Close——hide-to-tray 拦截下未批准的 Close 会吞成隐藏，与托盘退出同一条顺序契约（记序 fake 钉住）。`ryn.json` 的 `desktop: true` 能力面已覆盖新命令。

## Alternatives considered

- **在诊断导出器里做内容过滤**：落败——出口不止 zip 一条（stdout/落盘/回显），单点收口在 HostLog 才全覆盖；导出器保持白名单结构防护即可。
- **脱敏放 companion 页面层**：落败——页面层看不到宿主日志管道，且 companion 自身可能是事故源（历史缺 apply 白屏）。
- **恢复页做成可交互的完整文档站**：落败——恢复时刻 WebView 内 dsh 已死，复杂前端徒增漂移面；静态单页 + 两个动作已覆盖求助所需。
- **重试按钮**：落败——监督器本就循环自动重启并导航回真实 UI，手动重试与自动机制竞态；页面明示「自动重试」语义。
- **退出直连 window.Close 不过闸门**：落败——见 Decision 3，会复发「退出变隐藏」。

## Consequences

- 日志可读性可能因过度脱敏受损（如长 hex 摘要被遮）——方向性安全，遇到具体误伤再调键名清单。
- 头行遮罩吞行的设计意味着同一行 Cookie 之后的内容不可见——真实日志中敏感头通常独立成行，接受该边界。
- 恢复页仅在 dsh 崩溃路径出现；随包插件安装触发的重启沿用旧一行转圈（非故障语义）。
- 测试：SecretMasker 分层/组合/无误伤 + HostLog 落盘集成；RecoveryPage 注入安全（裸 `<script>` 必须以 `\u003C` 形态存在）/空尾部/按钮接线；退出路由记序契约。

## Related

- [托盘与关闭最小化](../architecture/2026-08-24-shell-tray-hide-to-tray.md)：关窗闸门与退出顺序契约的出处。
- [shell 可观测性与诊断](../architecture/2026-08-24-shell-observability-diagnostics.md)：HostLog 单点出口与诊断 zip 白名单的前置决策。
- [companion invoke 帧契约](2026-08-24-companion-invoke-frame-contract.md)：恢复页按钮消费 invoke 帧的解析约定来源。
