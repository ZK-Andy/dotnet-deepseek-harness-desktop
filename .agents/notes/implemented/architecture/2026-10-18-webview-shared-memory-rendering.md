# Agent Note: Linux 帧传递走 Ryn 的配置键，默认共享内存呈现

Status: implemented

Review: FULL/2026-10-18/R1=ok R2=ok R3=ok
Review: FULL/2026-10-18#2/R1=ok R2=ok R3=ok
Review: FULL/2026-10-18#3/R1=ok R2=ok R3=ok

Related: 上游 `Yupmoh/Ryn v0.38.0`（`RynApplicationBuilder.BindOptionsFromConfiguration` 绑定 `Ryn:LinuxRenderingMode`、`ApplyProcessWideOptions` 落 `WEBKIT_DMABUF_RENDERER_FORCE_SHM`，均按 `v0.38.0` 标签源码核对）+ 上游 `docs/gpu-rendering.md` + 本仓库 [cookbook](../../../../docs/cookbook.md)「高强度负载下 GUI 冻结的判别」（同族渲染开关 `HardwareAcceleration`）。

## Problem

Linux 桌面上整棵宿主进程树在空载一百分钟后仍持有 2.07 GB 记账内存（一次取样：`memory.current` 2070 MB＝匿名 1562 MB + 页缓存 478 MB + 内核 62 MB，swap 0，scope `app-gnome-deepseek-harness-desktop`【探索性，n=1】）。dsh 子进程经市场更新换代后九十秒内重新长到同一量级，说明稳态是重新分配出来的而非残留；webview 侧自启动起未被触碰，换代路径覆盖不到它。

抬高这 1.5 GB 匿名内存的机制有两条可能，均【推断 · 未证】：①WebKitGTK 的 WebProcess 堆只增不减（上游长期未修，同类案例 `webview/webview#421`、`cjpais/Handy#1279`）；②DMA-BUF 导入纹理在窗口可见期间被保留，抬高宿主侧原生内存（Ryn 自己记录了这条路径）。宿主此前从未设置任何渲染选项，一直走 Ryn 的 `Auto`（DMA-BUF 快路径）——即②这条成因从未排除过。

## Decision

`LinuxRenderingMode` 由**上游框架键** `appsettings.json` 的 `Ryn:LinuxRenderingMode` 承担，安装包发 `"SharedMemory"`；宿主代码不再对该属性赋值——`ConfigureOptions` 回调恒最后执行并覆盖配置绑定，赋值会让框架键静默失效。写 `"Auto"` 即回退——改的是安装包默认值、随发版交付，宿主不提供运行时开关。

Ryn 在 `Build()` 中段的 `ApplyProcessWideOptions` 一步把该选项转写成进程环境变量 `WEBKIT_DMABUF_RENDERER_FORCE_SHM`（`RynApplicationBuilder.cs:133`；WebKitGTK 只认初始化期的环境变量），宿主自己没有新增任何 env 写入。`Ryn opts` 行回显绑定值（`LinuxRendering=` 字段）；宿主不写该 env。

## Alternatives considered

- **自建 `WebViewOptions`（`WebView:ForceSharedMemory`）+ 组合根 bool→枚举映射**：本批先按此实现并过门禁，评审判为重复第二家且有害——Ryn 0.38 已绑定 `Ryn:LinuxRenderingMode`，而 `ConfigureOptions` 回调恒胜，等于用私有键静默遮蔽框架键；整套选项类型、装载缝接入与 12 例单测随之撤回。
- **安装器/启动脚本设环境变量**：落点与 Ryn 自己写的是同一支变量，但把产品行为藏进包外脚本，`HostLog` 看不到；否决。
- **`RynOptions.HardwareAcceleration = false`（软件渲染）**：代价是整页渲染质量与性能，且针对 GUI 冻结而非内存；保留为诊断手段。
- **经原生句柄设 `WebKitMemoryPressureSettings`（内存上限／阈值／kill 阈值／轮询间隔）**：引擎 API 存在（本机 `libwebkitgtk-6.0.so.4.19.4` 已导出 `webkit_memory_pressure_settings_*`），但壳拿不到 `WebKitWebView*`——Ryn 的 `GetNativeHandle` 只存在于 `Ryn.Core/Internal/*` 与 `Ryn.Plugins.WebViewPane/*`（`v0.38.0` 源码核）。要么上游开公开句柄，要么自行遍历 GTK widget 树（跨版本脆、把原生布局知识漏进壳层，违反 R3）；本批不做。
- **壳侧监护水位 + 主动 reload／重建窗口**：用户可见（丢页面态、可能打断进行中的回合），只能高阈值兜底。
- **换引擎或自带 Chromium**：Tauri 在 Linux 同为 WebKitGTK（不自带引擎）；自研引擎等于重写一遍 Ryn；已否决。

## Consequences

- 全体 Linux 用户的帧传递路径改变：共享内存呈现多一次拷贝、多一份带宽，DMA-BUF 快路径的收益让位给可预测的内存占用。Windows/macOS 不受影响（Ryn 的 Linux 分支早退）。
- **效果未证，本决定不承诺水位下降**。判断依据是使用体验而非采样：2026-10-18 拍板不做 A/B 观察（不跑两档长会话对比、不加采样负载），界面流畅度、画面拖影、输入延迟即判断依据；体感变差就在下一版把该键的默认值改回 `"Auto"`。
- 默认值只有一个家：`appsettings.json` 的 `Ryn` 节；回退即改这一处并随发版交付，没有第二处需要同步。

- **2026-10-18 实机观察**（用户主观反馈 + 一次读数，n=1【探索性】）：三重审核并发期间界面不再卡顿、帧率代价不可感知——这是本决定目前唯一的正向证据；同一时刻整树 `memory.current` 1692 MB（anon 1126 / shmem 282 / file 523 / swap 289，龄 20 分钟），对照切换前同机 70 分钟龄的 2070 MB（anon 1562 / shmem 109 / swap 0）。龄不同，两行**不可作 A/B** 读；可注意的变化是 shmem 上升（画面进共享内存）与 swap 从 0 变为非零（shmem 页可换出）。
