# Agent Note: companion-update-settings-section

Status: implemented

## Problem

自更新此前唯一入口是侧栏 ready 圆钮：仅 `ready` 态可见，用户没有任何主动「检查更新」的手段（设计期定过「启动检查一次 + 保留手动入口」，手动入口顺延未做，即遗留项①的后半）。且页面状态帧不含当前版本号、error 态不带失败原因——手动检查场景下「已是最新」相对什么、「失败」败在哪都无从展示。

## Decision

伴生插件注册 `settings.section`「桌面更新」设置页（order 50，市场之后），作为自更新的常驻手动入口；宿主状态契约小幅扩展支撑页面信息：

- 页面内容：当前版本行 + 状态行（尚未检查/检查中/下载中 vX.Y.Z/已最新/vX.Y.Z 就绪/安装中）+ 「检查更新」按钮（busy 态禁用）；`ready` 追加「立即安装并重启」主按钮；`error` 状态行显宿主传回的具体原因。
- 契约扩展：`UpdateState.ToJson` 增加 `current` 字段（`Transition` 单点补齐自 `_currentVersion`，推送与订阅回调统一发补齐后的帧）与 error 态 `message`（`JsonEncodedText` 转义）；旧字段形状不动，向后兼容。
- dev 门禁（自更新栈未装载、路由未注册）：`getState` invoke 失败 → 区块页内显示「不可用」提示行，导航项保留（用户拍板页内提示而非整项隐藏）。
- 插件 version `0.0.1 → 0.0.2`：首次践行版本感知升级的 bump 约定，随下一版壳分发即构成该机制的首个真实升级样本。

## Alternatives considered

- **getState 失败即动态注销 section**：落败——需动态退注逻辑、复杂度高；页内不可用提示同样避免误导且保留入口可发现性。
- **error 只显笼统文案（不外发 message）**：落败——手动检查是用户主动动作，失败原因应当可见（fail loud 在用户端的延伸）；message 本就存在于状态机记录中，仅是外发。用户拍板显示原因。
- **overlay/toast 主动通知更新**：不采纳——设计期已定「不轮询、不发 toast」；本笔记只补被动入口，不引入主动打扰。

## Consequences

- 收益：「启动检查一次 + 保留手动入口」设计闭环；当前版本与失败原因对用户可见；版本感知升级机制获得首个真实触发样本。
- 代价/风险：`current`/`message` 属新增松散契约字段，旧壳配新插件时缺失——UI 已按可选处理（无则省略行/笼统文案）；错误细节外发给本地 loopback 页面，暴露面可忽略。
- 验证：`dotnet test` 152/152（新增 ToJson 契约 4 例 + Transition 补齐 Current 回归 1 例）；`client.js` `node --check` 通过；实机验收随下次发版。

## Related

- `2026-08-22-desktop-shell-self-update`：状态机、命令路由与侧栏按钮本体。
- `2026-08-22-companion-plugin-version-aware-upgrade`：本次 version bump 即其约定首次执行。
