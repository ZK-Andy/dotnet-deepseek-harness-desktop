# Agent Note: aot-json-source-generation

Status: implemented

## Problem

v0.3.6 后某次会话基线核对的全量构建暴露 4 个警告（增量构建与 CI 均不可见）：`RecoveryPageBuilder` 用反射式 `JsonSerializer.Serialize<TValue>` 序列化恢复页 payload 与骨架，而 csproj 钉了 `PublishAot=true`——IL2026/IL3050 正是「裁剪/AOT 下可能坏」的真实契约标注，不是噪音。炸点是私有 POCO 的反射序列化路径，在 AOT 二进制中预期运行时失败；调用点在监督器崩溃恢复回调——**恢复页恰好在它存在的唯一时刻（dsh 崩溃）渲染不出来**。测试全绿是因为 xunit 跑 JIT；实机复验挂账里的「恢复页三按钮」一旦验到大概率就是这个雷。

顺藤盘点发现这不是孤例偏差而是惯例失效：全仓共 11 处 JSON/JS 字面量产出点靠手拼字符串 + `JsonEncodedText` 转义维持 AOT 安全（UpdateState.ToJson、FileReadyPersistence、RunMarker、三个横幅脚本的 JsString、四个命令路由的错误/状态帧、托盘偏好落盘），每处各自重实现转义与键名——对照批新代码自然写出反射调用正是该惯例守不住的证据。

## Decision

1. **单一源生成上下文成为宿主帧/持久化文档的 JSON 序列化通道**：新增 `Services/AppJson.cs` 的 `AppJsonContext`（`JsonSerializerContext` 源生成，`GenerationMode=SerializationOnly`，CamelCase 命名策略），全部帧 DTO 以 internal record 注册于此单文件清单——漏注册编译期即失败（fail loud 早于运行时）。迁移面：恢复页 payload/骨架、UpdateState 推送帧、ready.json 落盘、run-marker.json、托盘偏好文件、自启/关托盘状态帧、诊断导出 path 帧、五处错误帧。范围例外：profile 清单常量模板（DesktopProfileBootstrap 手拼，缩进排版须与上游 initProfile 模板逐字对齐）与 Utf8JsonWriter DOM 合并面（MarketInstallHelper）不经本通道。
2. **线协议对齐既有帧形状**（companion 与持久化读方零感知）：键名由属性名经 CamelCase 推导；`UpdateStatus` 保留手动小写（`ToString().ToLowerInvariant()`）；`message`/`current` 可空字段挂 `WhenWritingNull` 条件忽略；声明序即键序。既有精确串断言（UpdateStateJson/CloseToTray/偏好文件子串/恢复页转义形态）原样通过即为回归证明。唯一已知字节差异：marker `startedAt` 由 `ToString("o")` 的恒 7 位小数节变为 STJ round-trip 形态（尾随零可省略）——无读方消费该字段精度（Release 只认 token 键）。
3. **JS 字符串字面量嵌值统一走 `AppJsonContext.JsString`**：源生成 String 序列化 + 默认编码器（`<` 与非 ASCII 均 \u 转义），横幅（版本底线/自更新就绪/崩溃取证）与恢复页骨架共用；null 输入 `ThrowIfNull` fail loud——序列化 null 会输出裸 `null` 字面量，在 JS 字面量位置是静默陷阱。
4. **解析面不动**：桥接回包、ready.json/marker/偏好读取继续走 `JsonDocument`（桥接有再序列化怪癖，见 [companion invoke 帧契约](2026-08-24-companion-invoke-frame-contract.md)）；源生成上下文只管序列化方向。

## Alternatives considered

- **反射序列化照用 + NoWarn 压制**：落败——AOT 下不支持是运行时真实约束而非告警噪音；压掉等于把恢复页炸点留到用户崩溃时刻。
- **维持并推广手拼惯例**：落败——漂移已实际发生（对照批新代码写出反射调用且三路评审未拦）；每处重实现转义易错；键名字符串无编译期契约，改错只有联调能发现。
- **枚举走 `JsonStringEnumConverter`（CamelCase）替代手动小写**：落败——`UpToDate` 经命名策略变 `upToDate`，而 companion 契约是全小写 `'uptodate'`（client.js 状态机 switch），静默破坏「已是最新」显示；保留手动小写让契约只活在一处。
- **按域拆多个上下文（Update/Tray/Shell 各一）**：落败——注册总量约 10 个类型，单文件清单一眼全览全部帧形状；拆散反而把「宿主到底发哪些帧」藏进各域。
- **GenerationMode 取默认（Metadata+Serialization 双生成）**：落败——本项目从不经上下文反序列化，双模式徒增产物；SerializationOnly 下误用 `Deserialize` 缺元数据直接抛异常，方向正确。
- **重命名两个嵌套 `StateFrame` 以消解源生成属性撞名**：落败——类内名字冗余（`AutostartCommandRouter.AutostartStateFrame`）且波及声明与调用点；`TypeInfoPropertyName` 把消歧字符串放在 `typeof(...)` 同一属性行，自文档化且引用侧拼错编译即失败。

## Consequences

- 新增 IPC 帧/持久化文档的工作流变为「定义 DTO → 在 AppJsonContext 加一行注册」，键名改动即线协议改动，须对照 client.js 消费侧。
- 序列化行为与手拼版的等价性由测试面钉死：既有精确串断言全数保留，另含 marker 契约键、错误帧精确形状、autostart 线形 pin、JsString 转义边界。
- 恢复页在真机 AOT 包中的可用性仍属实机复验挂账项——本次移除的是确定性破坏源，不等于完成复验。

## Related

- [诊断脱敏与恢复页三件套](2026-08-26-diag-masking-and-recovery-page.md)：触发发现的恢复页 payload 所在批次；其 payload 序列化机制现归本通道。
- [companion invoke 帧契约](2026-08-24-companion-invoke-frame-contract.md)：解析面不走源生成上下文的原因（桥接再序列化怪癖）。
- [桌面壳自更新](../process/2026-08-22-desktop-shell-self-update.md)：`UpdateState.ToJson` 推送帧的引入批次。
- [companion 更新设置区](../feature/2026-08-22-companion-update-settings-section.md)：`current` 字段与 `JsonEncodedText` 转义的引入批次（该机制现归本通道）。
