# Agent Note: 收养市场接力的 dsh 续任者（运行时交接）

Status: implemented

Review: FULL/2026-09-12/R1=ok R2=ok R3=ok

## Problem

实机事故（2026-09-12 02:03–02:04，`host.log` / `.dsh-market/log.ndjson` / helper tmp 日志三方对齐）：

- 02:03:25 市场装 `dsh-opencode-session@0.1.1`；02:03:28.479 市场记 `restart scheduled pid=9884 helper=11287`。
- helper（detached node）等首选端口 36111 空出后复演壳的 spawn 命令行拉起**续任者** 11294，并把 `dsh web: http://127.0.0.1:36111/?token=loc-…` 写进 `/tmp/dsh-market-restart-2026-09-11T18-03-28.out.log`（err 日志为空 = 绑定成功）。
- 壳的监督器 02:03:30 见子进程退出，按首选端口 36111 spawn：此刻端口仍空，但市场续任者更早 bind（02:03:42 已在服务 36111），壳这次尝试注定失败；而壳的等待逻辑既不看 stderr 也不看子进程退出，白等满 60s，02:04:42 回退 `--port 0` → 漂移 44867，origin 变化 = 上一会话选中态丢失。（60s 是壳的等待窗，不是子进程自行退出的时刻。）
- 端口被占的 dsh 要到 spawn 后约 48s 才打印 `EADDRINUSE` 并 exit 1（独立对照实验 n=2：0.1s 轮询下首见签名 47.9s、自退 48.1s；设置 = 真实 home + `dsh --profile dotnet-desktop --port <被占端口>`）——**签名不提供早期预警**，它的价值是把失败分类为端口冲突。
- 11294 从来不是壳的子进程：`_process`、`Kill(entireProcessTree)`、`.dsh-pid` 记录都不覆盖它；且 `.dsh-pid` 随即被本次收尾的 spawn（11480）覆盖，冷启动 token 复验因此永久看不见它 → 首选端口长期被占 → 每次重启再漂移。

根因：市场对**未被识别为受管宿主**的 dsh 保留一键自重启——壳侧的 dsh 父进程是壳，`detectedSupervisor()` 只认 systemd，真机 `/dsh-market/api/v1/capabilities` 回 `"supervisor":null,"managedBy":"market"`；而壳把「子进程退出」一律当崩溃并抢同一个端口。两个重启者抢同一个端口，输者留下无人认领的残留。原生 `dsh web` 不复现：那里只有市场一个重启者，等端口空出后用同端口原地接力。

同族缺口两处：①失败尝试的子进程无人回收（存活到 ≈48s 才自退，见下条实测）；②`.dsh-pid` 单记录被下一次 spawn 覆盖后，记录路径对旧 token 失明。

## Decision

**壳侧单方解决，市场功能零改动**：把「抢输端口」变成可用结局，判据一律靠可证血统。

1. **血统判据**（`RuntimeLineage`）：锚点 = spawn 时注入的 `DSH_DESKTOP_SPAWN_TOKEN`（市场 helper 与续任者由 dsh 以 `env: process.env` 转发，故同属血统）＋生效 `DSH_HOME` 一致＋命令行形状＋**运行时面排除**。缺一不动——零误杀优先。
   - 命令行形状：helper 判据 = 市场重启日志名前缀 `dsh-market-restart-`，或 `node -e` + `restart` 形状兜底（上游改名时 helper 的 `node -e` 源码内嵌 `--profile`，会被形状判据误认成新生服务端而被收养——兜底只放宽「收割」，是刻意保留的改名韧性）；其余 `--profile <desktop>` 形状为服务端。
   - 运行时面排除三态化：候选与本壳在管运行时（`_process` 或收养的续任者）的父链关系分「可证在内 / 可证在外 / 不可证」，**只有可证在外才进残留面**；在管树的后代（同一次 spawn 的整棵子树共享同一 token，只比 token 会把在跑的 MCP/后台作业当残留）、以及**在管运行时的祖先**（收割其整树会连带杀死在管运行时；收养场景下市场 helper 正是续任者的父亲）一律排除；父链读不到/自环/超深同样排除（宁可漏杀）。
2. **端口冲突处置**（`RuntimeLineage.PlanPortConflict`，纯判定）：**端口此刻仍有回环监听**且存在「比刚退出那个运行时更晚诞生」（`StartTime` 比较）的血统服务端 → **收养**；更早的血统残留与市场 helper → **收割后重试首选端口**；端口已空且无残留 → 直接重试首选端口；占用者无血统 → 回退 OS 分配＋漂移告警（观测位语义不变）。两条不变量：①收养以「端口在服务」为前提——续任者尚未 bind 或已死时收养会导航到死端口，此时落回收割路径；②**收养时的收割面绝不含续任者自身、其祖先或父链不可证者**（见 Alternatives「为什么不能只调换收割与收养的顺序」）。
3. **收养语义**：登记 pid 与其血统 token、把 `.dsh-pid` 改指续任者（壳之后异常死亡仍可复验收敛）、`Stop`/`Dispose` 时整树收割（失败留痕）、`WaitForExitAsync` 以 1s 轮询**判活并复验 token**（pid 复用会让「`/proc` 存在」恒真、崩溃恢复静默失效；token 读不到时按仍在运行处理，宁可晚恢复也不把活着的运行时判死）；导航回**裸 origin**（不带 `?token=`）——dsh 会话 cookie 由落盘持久密钥签名、按 authority（host:port）绑定（`dsh-client-connection` 的 `initializeSecret`/`authorizeIndex`），同端口重启后 WebView 里那枚 cookie 仍有效，壳也不需要猜续任者的 per-process token。
4. **失败尝试即时回收**：失败信号 = stderr 命中 `EADDRINUSE` 且点名本次端口（端口后不得再接数字，故 `:3611` 不命中 `:36111`），或子进程退出——两者实测几乎同时（≈48s，见 Problem 第二条），签名的作用是**分类失败原因**而非提前预警；任一命中即整树回收该子进程。60s 超时降为兜底。
5. **收敛时机**：冷启动（`.dsh-pid` 记录复验＋血统扫描并用，且该次已全量收敛 → 同一次启动不重复扫描）、启动成功后（非冷启动时；抢端口输给壳的市场 helper/续任者此刻正等端口空出，就地收割）、端口冲突处置中（计划里的收割面）。
6. **边界**：不改市场任何配置/代码/开关（一键重启功能保留）；非 Linux 无统一进程环境读取 → 血统判据退化为空集，不收养、不扫描，只留 `.dsh-pid` 记录路径（残差见 Consequences）。

## Alternatives considered

- **关掉市场自重启（`allowRestart: false`，经壳自持 `--patch` 覆盖层）**：机制实测可行（假 home 跑 `dsh --dump-config`：overlay 命中 `dsh-market` 行；市场未装时只 warn 不阻启动；启动后可用 `capabilities` 断言 `features.restart`）。落败：把「一键重启」这个市场功能从用户手里拿走属产品面倒退，且市场设置开关在运行期仍能把它打开，覆盖层并非硬保证。
- **实现上游 desktop 服务契约（companion 提供 `desktopProfiles`+`desktopPnpm`）**：市场会走原生 Desktop 分支（`allowRestart` 由设计强制 false，安装经壳服务）。落败（本次不做）：契约是 Anywhere Labs 一家 client 的私有面、安装路径改道、失败面扩大，属 feature 级；留 follow-up。
- **端口占用者裸杀**：落败：端口不携带身份，误杀不可逆（ADR [child-process-reaping-port-drift](2026-08-26-child-process-reaping-port-drift.md) 已否）。本决策保留「按端口占用者识别」，但把身份换成可证血统，端口只作事实探针。
- **只给旧子进程加强制 SIGKILL**：落败：.NET `Process.Kill()` 在 Unix 即 SIGKILL，`entireProcessTree` 已覆盖树杀，缺的是「杀谁」不是「杀多重」。
- **进程组/session 收割（`kill -- -PGID`）**：落败：实测 dsh 与本机 gnome-shell 同 pgrp/sid（2645，【探索性，n=1】），按组杀会打死整个桌面会话；dsh 沙箱子进程另有独立 sid，本就不在组内。
- **固定等待窗让位（先等续任者起来再决定）**：落败：给真崩溃恢复叠加固定延迟，且实测 `EADDRINUSE` 直到 ≈48s 才出现（签名做不了提前决策）。真正有信息量的是「市场 helper 已出现」这一正向信号——记入 Deferred，本次不做。
- **收养前再等续任者 bind**：落败：端口冲突本身就是「已有人 bind」的证据（`IsLoopbackServingAsync` 复核即可），无需再等。
- **血统排除集用「本次 run 的 token 集合」**：落败：同一次 run 的失败 attempt 与在管 attempt 持两个不同 token，按集合排除会把该收的悬挂尝试当自家人；改按「在管运行时＋其父链后代」排除。
- **收养时读/猜续任者的 launch token**：落败：per-process token 本就不该被壳猜；持久 cookie＋authority 绑定已让裸 origin 可用。
- **复用市场 helper 的 tmp 日志识别接力**：落败：上游内部文件名与格式，非稳定契约；血统＋新生判据同效且不依赖它。
- **只调换「收割」与「收养」的先后顺序**：落败（评审 B1 实证）：市场 helper 是续任者的父进程，整树击杀辅助进程连带杀死收养目标；且收养后的「启动成功后收敛」会以同一机制再次连带杀死已收养的续任者（此时它是收养 pid 的祖先）——必须把「续任者自身及其祖先」从收割面剔除，换序无效。
- **收养不以「端口在服务」为前提**：落败（评审实证）：续任者尚未 bind 或中途死掉时会收养到死 pid、导航到无人监听的裸 origin，退化成「失败导航＋恢复闪屏」；以 `IsLoopbackServingAsync` 为准落回收割路径即可。
- **收养判活只看 `/proc/<pid>` 存在**：落败（评审实证）：pid 复用会让监督器永远看不到收养运行时退出（本仓用 token 复验防的正是同一风险），改判活＋token 复验。

## Consequences

- 市场一键重启继续可用且与壳不再互抢：端口冲突的三种结局（收养 / 收割后重试 / 漂移）都收敛到「有人服务且 origin 尽量不变」；抢端口输给壳时，市场 helper 与续任者被「启动成功后收敛」就地收割，不再留下悬挂进程。
- 失败尝试不再悬挂：此前每次失败留下一个存活到 ≈48s 的无人认领 dsh 进程，现在判定即整树回收。
- 端口冲突的等待代价仍在：抢输的尝试要走完对方的 bind 失败路径（实测 ≈48s），恢复屏覆盖这段时间。本决策买到的是「这段时间之后收敛到同一 origin 且不留残留」，不是「更快发现失败」；压缩该等待的正向信号（市场 helper 已出现）记入 Deferred。
- 代价：①冷启动与「非冷启动的启动成功」各一次、端口冲突处置一次 `/proc` 全量扫描（逐 pid 读 environ＋cmdline，毫秒量级）；②收养引入「在管但非子进程」的 pid——判活靠 1s 轮询＋token 复验、退出靠整树击杀（Linux 精确；其他平台不做收养）；③血统判据覆盖不到 dsh 沙箱内的后代（dsh 的 `scrubbedParentEnv` 剥掉全部 `DSH_*`）。
- 观测：新增日志行（失败原因＋处置动作＋残留条数 / 收养 pid / 收割 pid＋kind）；漂移告警文本与原语义保持一致。
- 非 Linux 残差：无血统判据 → 不收养、不扫描，仅 `.dsh-pid` 记录复验；首选端口被续任者占时仍按漂移处理。

## Deferred

- **残留/收养状态未进诊断包**：`DiagnosticsExporter.IncludedFiles` 白名单仍只收 host.log、端口状态文件与 run-marker，未新增 `.dsh-pid`（本次语义扩为「在管运行时，可能是收养的非子进程」，属 pid 面数据）。刻意不扩：本次未新增 home 内路径，诊断包不应在无决策的情况下扩收进程数据。**复访触发 = 出现首个「靠诊断包才定位得到」的残留案例**。
- **以 helper 出现作为接力意图信号**：端口冲突的等待代价（≈48s）来自「注定失败的 spawn 走完 bind 失败路径」。市场 helper 在旧 dsh 退出后约 1s 内即出现且带本壳 token，可作为「接力正在进行」的正向信号 → 此时改为等续任者 bind（有界）而非抢端口。本次不做：会引入新机制，需独立评审与测试；且它对**真崩溃**路径零收益（无 helper）。
- **dsh 沙箱内后代不可见**：`scrubbedParentEnv` 剥掉全部 `DSH_*`，本判据收不到这类孤儿。**跟进方 = [dsh 沙箱子进程孤儿泄漏](2026-09-12-dsh-sandbox-child-orphan-leak.md)**（已 implemented，本篇为判据盲区的补集收割）。

## Testing

- `RuntimeLineageTests`（19 个测试方法 / 22 个用例）：home 不符、无关命令行 → None；helper 标记优先于其内嵌的 `--profile`，且 `node -e`+`restart` 形状兜底可判 helper；端口签名须同时命中标记与端口且端口后不接数字；父链归属三态（Inside / Outside / Unknown，自环与超深归 Unknown）；运行时面排除（在管树后代、**在管运行时祖先**、父链不可证者，冷启动无在管时全收）；处置计划矩阵（新生续任者收养且无关残留入收割面 / **续任者祖先绝不入收割面（B1 回归）** / 端口无监听时不收养 / 更早残留收割重试 / 端口忙无残留回退 / 端口空无残留重试 / 无参照不收养 / 起始时刻未知不收养）。
- `HarnessRuntimeHostTests` 增 `BuildStartPsi_CarriesLineageTokenAndHome`：spawn 环境注入血统 token 环境变量 `DSH_DESKTOP_SPAWN_TOKEN`（`RuntimeLineage.TokenEnv`）与生效 `DSH_HOME`（血统判据、清扫与交接处置的共同前提）。
- 全量 `dotnet test` 569 通过（原 546 + 23 新）；`verify-code-health --enforce`、`verify-code-conventions --enforce`、`dotnet format --verify-no-changes` 全绿。
- 覆盖率基线（CI `build-test` cobertura）：本篇所在代码面（0.4.9，作业 `34641155703`）3718/6722 = `line-rate=0.5531`，口径见 [coverage-baseline-from-ci-cobertura](../testing/2026-09-12-coverage-baseline-from-ci-cobertura.md)——新增面以 `/proc` 探针与 host 交接接线为主，走实机验收路径而非单测，故总额随被测面扩大略降。
- 实机验收清单（发布后逐条过）：①市场装插件→点重启：URL/origin 不变、只有一个桌面 dsh、残留端口无占用、host.log 有收养行；②`kill -9` 在管 dsh：恢复不慢于今天；③端口被无关进程占：仍是漂移＋告警；④冷启动前人为留残留：被血统收割且 origin 不变；⑤人为制造端口冲突：失败尝试被立即收割、无悬挂残留。
- 取证方法可复现：`/tmp/dsh-market-restart-*.out.log`、`~/.dsh/profiles/dotnet-desktop/.dsh-market/log.ndjson`、`~/.dsh/logs/host.log`、`curl /dsh-market/api/v1/capabilities`、`ps -o pid,ppid,lstart,cmd` 与 `/proc/<pid>/environ` 交叉核对。

## Related

- [自更新退出路径确定性收割 dsh 子进程](2026-08-28-self-update-exit-reaps-dsh-child.md)：本决策扩展其缺口 B 的清扫判据（单一记录 → 记录＋血统扫描并用）。
- [子进程收割与端口漂移](2026-08-26-child-process-reaping-port-drift.md)：其 Alternatives 否掉「attach 存活 dsh」的理由是「探活猜归属」，本决策以血统可证＋新生判据取代该理由，方向仍不同（收养只针对接力产物）。
- [dsh 沙箱子进程孤儿泄漏](2026-09-12-dsh-sandbox-child-orphan-leak.md)：本判据的已知盲区（已 implemented，scope 收割补集）。
