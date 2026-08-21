# Agent Note: persist-web-port-across-launches

Status: implemented

## Problem

桌面端每次整 App 冷启动都回到新会话，用户上一次打开的会话不恢复。根因不在会话数据（正文服务端持久于 DSH_HOME，未丢），而在壳对 dsh Web 端口的管理：HarnessRuntimeHost._port 只是进程内内存字段，冷启动时为 null，StartCoreAsync(null, ...) 走 --port 0 由 OS 随机分配，每次启动 origin（http://127.0.0.1:<新端口>）都不同。dsh Web 端把「当前选中哪个会话」持久在 localStorage 键 dsh.sessions.current（上游 packages/client/runtime/src/client/sessions/service.ts：createSnapshotStore({}, {persist:{name:'dsh.sessions.current'}})，构造时 restored.sessionId 恢复选中；localStorage 按 origin（scheme+host+port）隔离），新 origin 读不到，restored.sessionId 为空，新开会话。既有的崩溃重启修复（80b6c0c）只覆盖进程内崩溃重启（内存 _port 复用同端口、origin 不变），对整 App 重启无效。

## Decision

把「稳定端口」从内存级提升为跨进程持久化：dsh 成功给出 URL 后把端口写入可写状态文件 <DSH_HOME>/.dsh-web-port；冷启动时先读该文件作为首选端口（StartAsync 的 preferred = _port ?? TryLoadPersistedPort()），并保留既有「固定端口被占回退 OS 分配」逻辑，成功后回写新端口。行为：

- 进程内崩溃重启：继续复用内存 _port（行为不变，同端口回写无副作用）。
- 整 App 冷启动：加载上次端口，同 origin，同 WebView localStorage，dsh.sessions.current 命中，恢复上一会话。
- 端口被其他进程占用/绑定失败/状态文件损坏：回退 OS 分配并回写（下次启动重新稳定）。
- 单实例天然成立：被占即回退，无需互斥锁。

持久化路径取 HarnessRuntimeHost.ResolveDshHome()（默认 LocalApplicationData/DeepSeek.Harness.Desktop/dsh，可写；DSH_DESKTOP_DSH_HOME 环境覆盖对测试隔离生效）。读写失败按 best-effort 处理（记日志、回退 OS 分配），不阻断本次启动：写失败只会让下次冷启动回新会话，与现状持平，不应因状态文件故障让整个应用起不来。

## Alternatives considered

- 改前端/服务端（dsh 侧支持记住上次选中的会话并服务端恢复）：落败——桌面壳应保持宿主负责系统集成，改 dsh 产物随上游升级易失，且 web 端无此问题（浏览器 origin 稳定），修复点应在壳的 origin 稳定性上。
- 把端口写进注册表/系统配置：落败——跨平台（Linux/macOS/Windows）无统一机制，DSH_HOME 已存在且必然可写，是自然归属处。
- 固定端口（每次启动绑同一个硬编码端口）：落败——多实例/端口被占时必须换端口，且无法收敛到端口确实被 dsh 接受的真相；记上次成功端口 + 被占用即回退更稳。
- 仅依赖 OS 分配端口 + WebView 侧注入恢复（壳注入 JS 把会话选中写进服务端/新 origin）：落败——注入方案要等页面加载后再跳转，白屏/竞态风险高，且破坏 WebView 与 dsh 的正常加载语义；复用 origin 是零侵入的正解。

## Consequences

- 收益：冷启动恢复上一会话（origin 稳定 → localStorage 命中），崩溃重启行为不变；逻辑全在壳内、可单测。
- 代价/风险：依赖 Ryn/saucer WebView 的 localStorage 跨整 App 重启持久（持久化数据目录而非 ephemeral 会话）——进程内崩溃修复（80b6c0c）已证明同 WebView 内跨 dsh 服务端重启 localStorage 存活；跨 WebView 重建的持久性需实机/E2E（门控 DSH_TEST_E2E=1 双实例同端口断言）复验，若实测为 ephemeral 再补给 WebView 配置持久 user-data 目录一步。端口文件损坏/被占时回退 OS 分配，退化为旧行为（新会话），不 fail loud 阻断启动。
- 验证：dotnet build 0 警告 0 错误；dotnet test 全绿（新增端口读写/损坏文件单测 + 门控 E2E 双实例同端口）；三部门禁全绿。真实桌面重启回上次会话需实机验收（沙箱渲染受限）。
