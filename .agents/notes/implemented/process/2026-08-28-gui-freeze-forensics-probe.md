# Agent Note: 高强度负载下 GUI 冻结取证探针

Status: implemented

> 放置路径：`.agents/notes/implemented/process/2026-08-28-gui-freeze-forensics-probe.md`
> class = `process`（工具/流程，交付可执行取样脚本而非产品行为）。
> `scripts/verify-adr-format.py`（含 `--self-test`）机器强制命名与头/骨架，新建即校验。

## Problem

DeepSeek Harness 桌面壳（Ryn/saucer → WebKitGTK）在高强度负载（后台并行多个 subagent 并发向 dsh Web UI 倾倒内容）下，用户观察到「窗口能动、内容不动」的页面冻结。该现象具三个判据叠加：Linux Wayland/GNOME + 无 GPU（`/dev/dri` 缺失，`XDG_SESSION_TYPE=wayland`）。

这类冻结是**瞬态随机事件**：恢复后 CPU 回落、现场信息全失。等恢复后再去 `top`/看 host.log 只能拿到结果，拿不到冻结进行时三类进程（壳宿主 / WebKit 渲染 / dsh 生成）的**相对负载**——而相对负载才是判别根因方向的一锤定音依据。没有常驻滚动采样，就无法回答「冻结到底是 WebKit 软件合成被拖死、还是 dsh 疯狂产出页面消化不迭、抑或宿主壳意外被占」。

## Decision

新增 `scripts/probe-gui-freeze.sh`：一个**常驻滚动采样取证探针**，专门为「高强度负载下 GUI 冻结」提供可判别的进程级现场。零行为变更（不改产品），产物是脚本 + 观测记录。

关键机制：

1. **两层采样结构**：
   - **基础层**：默认 `1s`/tick，滚动保留最近 `ROLL_WINDOW=60` 个 tick 到 `OUT_DIR/.rolling.log`（环形，文件上限由滚动窗约束）。平时近零开销。
   - **触发层**：当壳宿主 %CPU 连续 `SUSPECT_TIMES=3` 次 `< SUSPECT_CPU_THRESHOLD=5` 判定疑似冻结 —— 立即把滚动窗拍平归档到 `freeze-event-<timestamp>/`，并从此**加密采样**（`0.2s`/tick）持续 `MAX_FAST_SECONDS=30` 秒或冻结解除。

2. **取样对象（三类进程，按 args 宽松匹配，找不到标 `none` 不中断）**：
   - 壳宿主：`DeepSeek.Harness.Desktop`；
   - WebKit 渲染：`WebKitWebProcess` / `WebKitNetworkProcess` / `webkit2gtk` 变体；
   - dsh 生成：`dsh` / `bin.js` / `@deepseek-ai/dsh` / `deepseek-ai/dsh`。
   每个对象抓 `PID`、`%CPU`、`%MEM`、`RSS KB`（`ps -eo pid,%cpu,%mem,rss,args` 按 args 匹配）。

3. **线程状态采样**：`/proc/<pid>/task/<tid>/stat` 的 `R`/`S`/`D` 三态计数。`D`（不可中断内核等待）是「卡在 IO/内核铁证」，能区分「忙渲染（R 多）」vs「被内核阻塞（D 多）」。无权限时输出 `-` 跳过。

4. **触发后产物目录 `freeze-event-<timestamp>/`**：`rolling-before.log`（冻结前滚动窗）、`during.log`（加密采样段）、`ps-tree-full.txt`（`ps -efL` 全量进程树快照）、`host.log.tail`（`$DSH_HOME/logs/host.log` 尾部 40 行）。`DSH_HOME` 默认 `$HOME/.dsh`，环境变量 `DSH_DESKTOP_DSH_HOME` 可覆写。

5. **健壮性（探针绝不被 `set -euo pipefail` 中断）**：进程不在跑时 `grep` 返回 exit 1，叠加 `pipefail` 会让 `$( )` 失败；这不是错误而是「无匹配」，故所有取样管道 `|| true` 兜底——**观测工具绝不因找不到进程而自毁**。`detect_and_act` 对空 `cpu` 判据 `[ -n "$cpu" ]` 守卫，避免采样环境异常（壳不在跑）时误触发。

6. **接口**：`--self-test`（离线自检，验证参数合法 + 进程模式可被 `grep -E` 消费 + 容错路径输出 `none`）、`--once`（单次采样冒烟）。环境变量 `FREEZE_OUT_DIR` / `FREEZE_TICK` / `FREEZE_FAST_TICK` / `FREEZE_ROLL_WINDOW` / `FREEZE_SUSPECT_CPU` / `FREEZE_SUSPECT_TIMES` / `FREEZE_MAX_FAST` 覆写默认值。

### 边界与刻意取舍

- **不接 `DSH_DEVTOOLS=1`**：用户拍板不接。DevTools Performance 火焰图虽能给出 JS 主线程 vs painting 的细粒度，但会显著增加负载、**污染「高负载」现场**，使取证失真。探针聚焦进程级 CPU/线程状态，以系统采样为准，避免观测工具干扰被测对象。
- **复用 `--export-diagnostics`**：探针**不耦合**既有的 `--export-diagnostics` CLI（`Program.cs` 兜底导出），而是直接读 `$DSH_HOME/logs/host.log` 尾部。理由：`--export-diagnostics` 会额外 spawn dsh，在正常 dsh 运行的冻结场景下属额外副作用；且探针定位为**系统侧观测**，保持与产品进程最小耦合，缩小风险面。
- **页面探针（`PageHealthMonitor`）不纳入**：页面是否 alive 由宿主 `PageHealthMonitor`（阶段 1，只读探针）承担，本探针只做进程级 CPU/线程，两者职责分离。若宿主未开探针，探针仅以「壳 CPU 低」作弱触发信号。

## Alternatives considered

- **接 `DSH_DEVTOOLS=1` 抓 Performance 火焰图**：能区分 JS 主线程忙 vs 绘制忙，细粒度最高。落败：DevTools 开销会显著抬高负载，**污染高负载取证现场**，且需要渲染层人工干预，非自动化。用户拍板不接。
- **复用 `--export-diagnostics` 作取证入口**：省去自读 host.log 的代码。落败：该 CLI 会额外 spawn dsh、产生副作用，且在冻结场景下再 spawn 一次子进程会加重现场扰动；探针保持纯系统侧最小读取更可信。
- **依赖 `top`/`pidstat` 交互式人工观察**：最简单。落败：瞬态冻结恢复后现场丢失，无法自动化记录「冻结进行时」的样本，只能拿到结果拿不到过程。
- **把探针产品化为 `scripts/` + `docs/cookbook.md` 判别条目**：选此项（已实现）。落败项是「只留 `.plan/` 一次性脚本」——用户拍板走 ADR 正式立项，让后续会话能复用并被 verify-* 门禁捕获。

## Consequences

**代价**：新增一个常驻可选脚本（`scripts/probe-gui-freeze.sh`），非产品路径、零行为变更；`--self-test` 纳入文档门禁由手工触发（探针自身无 CI 自动跑，因它面向真机冻结场景，CI 无此进程环境）。用户需在真机复现时手动启动该探针。

**收益**：冻结不再「恢复即丢现场」。探针在冻结发生瞬间自动归档滚动窗 + 加密采样 + 进程树 + host.log 尾部，用三类进程的相对负载回答「是 WebKit 饱和 / dsh 满负荷 / 宿主被占」，配合 `D` 态线程计数区分「忙渲染」vs「内核阻塞」。这为后续「调 `RynOptions.HardwareAcceleration`」或「降前端渲染负载」的对策决策提供可判别证据，避免盲调。

## Testing

- `--self-test`：离线自检，覆盖参数合法性与进程模式可被 grep 消费、容错路径输出 `none`。本机实测通过。
- `--once`：单次采样冒烟（`sample_tick` 输出 [shell]/[webkit]/[dsh] 三行 + [shell-cpu]）。本机实测输出 `[shell-cpu] 0.0`、其余 `none`（沙箱无对应进程），exit 0。
- 归档路径隔离验证：直接调用 `trigger_freeze_event` 实测产出 `freeze-event-<ts>/{rolling-before,during,ps-tree-full,host.log.tail}` 四件套。
- 无对应进程时探针不中止（`|| true` 兜底），不产生 `.freeze-events` 误报。

## Related

- 方案来源：`.plan/取证探针-高强度GUI卡死-2026-08-28.md`（本地工作文档，gitignored）。
- 现状基线：`.plan/适配方案.md` 两步模型 §9、`RynOptions.HardwareAcceleration`（Ryn 文档原文点名 Linux/WebKitGTK 无 GPU 为已知敏感区）。
- 判别条目：`docs/cookbook.md`（见同批次提交）。
