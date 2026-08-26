# Agent Note: shell-observability-diagnostics

Status: implemented

## Problem

桌面态 stdout 不可见，诊断信息曾经只有补装任务单点落盘；批次二立项时四类缺口：①壳侧诊断流分散——HostLog 已统一双写但 supervisor/update/横幅等仍走裸 Console，且自更新链路状态变化无留痕、dsh 子进程 stderr 只留 40 行内存尾巴随进程消失；②用户求助路径断裂——「闪退进不了界面」时拿不到任何证据，也没有一键打包手段；③非受控退出（断电/被杀/原生崩溃）无痕迹，下次启动无从区分「上次崩了」还是「上次正常关」；④上游 dsh 无文件日志设施（源码核实），`<home>/logs/` 实际归桌面所有，需要明确所有权与形态。

## Decision

四件套落地，全部围绕既有 `<DSH_HOME>/logs/host.log` 单通道展开：

1. **日志主体收口**：RuntimeSupervisor 的 log 回调从 Console 换成 `HostLog.Write`，恢复分支追加 dsh 子进程 stderr 尾部（8 行）落盘——子进程死前最后一句话不再丢失；自更新状态机经 `onTransition` 单点把每次状态变化写入 host.log；HostLog 增加简单尺寸轮转（超 5 MB 滚动为 `host.log.old`，保留一代）防无限增长。
2. **一键导出诊断 zip**：新命令 `desktop.diagnostics.export`（`DesktopDiagnosticsCommandRouter`，走既有 `desktop` 能力面），companion 设置页「导出诊断信息」按钮触发（点击即隐私确认——内容白名单仅 logs/port/state.txt，显式排除 credentials/sessions/profiles/storages/updates/pnpm 目录）；zip 落用户文档目录（跨平台稳定可见），返回绝对路径给页面展示。
3. **无 UI 兜底 CLI**：`DeepSeek.Harness.Desktop --export-diagnostics` 在一切启动逻辑之前执行导出并打印路径后退出——不 spawn dsh、不开窗、不做 dev 隔离，覆盖闪退场景。
4. **active-run 崩溃取证 marker**：启动时原子写 `<home>/logs/run-marker.json`（owner token + pid + 时间戳，临时文件 rename 发布）；已存在的 marker 若是符号链接/重解析点一律删除重建（不穿链接写）；正常退出仅当 token 匹配才清除（owner 幂等）；下次启动发现遗留 marker 即判定上轮非受控退出——记日志 + 横幅提示引导导出诊断。已知取舍：dev 与正式版并存时共享同一 home 的两实例共用单一 marker，误报窗口接受（预览期实例数少，ADR 记录在案）。多实例覆盖诱因已由单实例仲裁结构性消除（见 [单实例 launcher 激活](2026-08-26-single-instance-launcher-activation.md)），marker 单文件语义与判定不变。

## Alternatives considered

- **引入成熟日志框架（Serilog/NLog/Microsoft.Extensions.Logging）**：落败——双写+轮转共 ~40 行即满足需求；引框架带来依赖与配置面，违背轻壳定位。
- **对接 dsh 日志设施而非另造轮子**：前提不成立——rc.2 源码核实 dsh 无文件日志设施；退化为「沿用其惯例目录名 logs/ 并声明所有权」。
- **诊断 zip 放系统临时目录或 home 内部**：落败——临时目录用户找不到且易被清理；home 内部与敏感数据同层，误分享整目录时泄漏面大。放用户文档目录是「文件管理器可见」的直接解。
- **marker 按 pid 多实例化（每实例一个文件）**：落败——崩溃判定语义变复杂（需清理孤儿集合），预览期单实例语义够用；owner token 已防跨实例误删。
- **崩溃提示做成持久设置项/遥测上报**：落败——无遥测立场（数据不出机）；持久开关属迁移管控式复杂度。

## Consequences

- 壳侧诊断单通道闭环：supervisor/自更新/横幅/补装全部进 host.log；子进程 stderr 尾部在恢复分支落盘——崩溃取证不再依赖内存尾巴。
- zip 白名单按条目枚举而非目录通配：新增敏感路径须回查清单（代码注释钉住）；轮转只保一代，极端刷屏丢早期日志属可观测性批内有意取舍。
- marker 判定把用户杀进程也算非受控退出：文案已避免暗示应用故障（横幅措辞为事实陈述+引导导出）。
- CLI 导出失败以非零退出码 fail loud；文档目录不可写时回退 `<home>/diagnostics/` 并留痕（沙箱只读 /home 实测回退链）。

## Related

- [dev 运行时隔离](../process/2026-08-22-dev-runtime-isolation.md)：`--export-diagnostics` 刻意先于 dev 隔离执行——CLI 取证不关心实例形态。
- [companion 更新设置页](../feature/2026-08-22-companion-update-settings-section.md)：诊断区块（order 51）挂同一 settings.section 机制之下。
