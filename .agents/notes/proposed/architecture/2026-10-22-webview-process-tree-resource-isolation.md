# Agent Note: 页面级资源失控不得拖死整机（WebView/dsh 进程树隔离）

Status: proposed

## Problem

2026-10-22 在本机（Fedora 44、webkitgtk6.0 2.54.1、DSH Desktop 0.6.14、dshmarket 1.66.47）复现：打开插件市场的「主题」页，整台桌面在 20 秒内失去响应——窗口、合成器、托盘依次停摆，只能重启。

实测（n=2 次发作 + 1 次对照，【探索性】；采样与命令见本笔记 Related 的上游 issue）：

- 渲染进程 `WebKitWebProcess` 以约 **115 MB/s** 增长：801 MB → 3066 MB / 22 s；同期抓栈时 `Rss 1.26 GB`、`Pss_Anon 1.11 GB`（私有匿名，不是共享内存或文件缓存）。
- 会话 cgroup 2.6 GB → 6.1 GB / 90 s；`hermes` 于 18:57:54 记 `system memory pressure is elevated`，gnome-shell 于 18:58:55 记 `Connection to xwayland lost`。
- 同期 WebKit 磁盘缓存（964 KB / 94 文件）与 :443 连接数（约 14）恒定；栈上热点是 `SkiaCPUWorker` ×4——没有下载、没有 JS 循环。
- 对照：仅从已安装的 `client/client.js` 删掉市场主题徽章那一行 `backdrop-filter`，同样操作变成 575 MB → 744 MB 后持平。

触发物在市场（上游 issue #980、PR #981 已修），但**失败形态与触发物无关**：任何第三方插件页面、任何依赖的渲染回归，都能把唯一的渲染进程堆到 GB 级，而进程树没有任何内存边界。把「发版前人工浏览一遍」当作防护，不可靠。

结构成因（前两条为实测观察，第三条为【推断 · 未证】）：

1. 壳只创建一个 WebView 承载全部 dsh Web UI（[Program.cs](../../../../src/DeepSeek.Harness.Desktop/Program.cs) 加载 `dsh web:` URL）；市场面板是同一文档里的 React 区域，渲染主线程即整个 UI 的主线程。
2. dsh server、壳进程与合成器同处一个 systemd 用户会话 cgroup（`session-3.scope`），没有 `MemoryMax`/`MemorySwapMax`。增长到 6 GB 时整机进 swap，托盘与合成器一起被拖住，`systemd-oomd` 的判据是会话压力而非具体进程树，没有可用的恢复路径。
3. 健康探针每 10 s 跑一次 `document.body.innerText`（[PageHealthMonitor.cs](../../../../src/DeepSeek.Harness.Desktop/PageBridge/PageHealthMonitor.cs)）：强制全文档 layout，在重页面上本身是额外负载；主线程被占时求值排队不返回，`dead` 计数也走不到有界恢复。本次事件中探针没有留下任何 dead 行，与该解释一致。

## Proposal

**决定：把「一个页面不得拖死整机」做成壳的硬约束，随 Avalonia 宿主迁移（ADR `2026-10-22-avalonia-host-migration`）一并落地；Ryn 宿主期不单独实现。**

理由：WebView2 / WebKitGTK / WKWebView 的进程边界不同（WebKitGTK 内容进程 vs WebView2 浏览器/渲染/GPU 进程 vs WKWebView 的网络与内容进程），限额该加在哪一层、哪个子进程归哪个 scope，必须在新宿主上实测；在 Ryn 上做完迁移后要重做一遍。

三项措施（按优先级）：

1. **进程树资源边界**：dsh server 及其 WebView 子进程归入独立 cgroup（`MemoryMax` / `MemorySwapMax` 可配置），越界由内核或 systemd-oomd 终止；壳的监督器与恢复面接手（`PageHealthRecovery` 的 dead→reload 通道已存在）。
2. **分账**：WebView 与 dsh server 分属不同 scope，恢复动作才能选对——杀页面进程是 reload，杀 dsh 是重启服务，两者对用户的可观察后果不同。
3. **探针降本**：页面每几秒把心跳写进一个 `data-*` 属性或全局变量，壳只读那个值；不再用 `innerText` 触发全文档 layout。

验收：一个故意分配内存的合成负载页验证「限额触发 → 恢复面出现 → dsh server 不被误杀」；三平台各跑一遍。

## Alternatives considered

- **在 Ryn 宿主上先做**：零迁移依赖，但迁移在即（ADR `2026-10-22-avalonia-host-migration`），两套宿主同时维护限额与恢复语义，且 Ryn 已停止演进。落败。
- **只靠上游修复**：本次触发物确实在市场（#980/#981），但修的是「这一次的模糊」，不是「下一次任何页面」。落败。
- **依赖 systemd-oomd 默认策略**：它已在运行，判据却是整会话压力，可能连桌面会话一起杀，且没有按进程树定位与恢复的路径。作为兜底保留，不能作为方案。落败。
- **插件侧自律/黑名单**（禁止 `backdrop-filter`、限制图片数等）：第三方插件不受本仓控制，黑名单永远落后于下一次触发物。落败。
- **卡住时给用户提示**：最坏形态下页面已死，提示渲染不出来。落败。
- **直接砍掉 WebView 里的重页面能力**（例如禁止市场渲染图片墙）：拿产品功能为工程约束买单，且约束不住别的页面。落败。

## Consequences

- 新增失败模式：限额会误杀正常的大页面（长列表、图片墙）。阈值需对照 0.6.14 的浸泡基线标定，并在迁移验收里固定下来。
- 恢复语义要写清楚：杀 WebView 进程 = 页面 reload；杀 dsh = 服务重启。两者的用户观感与数据影响不同，恢复面文案不能混用。
- 探针口径变更影响现有判据：`PageHealthMonitor.Parse` 只认 `text:<n>`，改心跳形态要同步改测试与诊断包快照。
- 文档面：`docs/architecture.md`（进程与资源模型）、`docs/testing.md`（新增验收）、`docs/cookbook.md`（本次实录）需在落地时同步。
- 市场侧的上游修复只止血：本机安装版仍是手工补丁，升级 dshmarket 即回到触发物；这条约束的存在理由不因上游修复而消失。

## Related

- [avalonia-host-migration](2026-10-22-avalonia-host-migration.md)：宿主迁移；本笔记的措施在其落地时实现。
- [webview-shared-memory-rendering](../../implemented/architecture/2026-10-18-webview-shared-memory-rendering.md)：`LinuxRenderingMode=SharedMemory` 与 WebKitGTK 内存现状。
- 上游触发物与实测记录：<https://github.com/dsh-market/dsh-market/issues/980>、<https://github.com/dsh-market/dsh-market/pull/981>。
