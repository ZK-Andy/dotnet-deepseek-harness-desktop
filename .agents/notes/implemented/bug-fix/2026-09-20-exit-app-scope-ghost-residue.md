# Agent Note: 退出后 GNOME app scope 幽灵残留（浏览器树 + dsh 下游）

Status: implemented

Review: FULL/2026-09-20/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

实机取证（2026-09-20，Fedora 44 / GNOME Wayland）：托盘「退出」后重启，`systemd-cgtop` 里旧实例的 scope 仍在：

```
app-gnome-deepseek\x2dharness\x2ddesktop-11839.scope   11 进程 / 115 线程 /  770 MB   ← 新
app-gnome-deepseek\x2dharness\x2ddesktop-2965.scope    20 进程 / 185 线程 / 1531 MB   ← 旧，仍活
```

旧 scope 是 07:19 autostart 起的实例（transient unit 07:19:11 建立，`Description=Application launched by gnome-session-service`）；其主进程 2965 已于 07:57:01 走有序退出（host.log `[tray] 有序退出` → `Ryn Run 结束`），全机只剩一个 `DeepSeek.Harness.Desktop`（11839），其 dsh（3374）也被回收（下一轮冷启动复验确认已不存在）。**幽灵不是壳进程，是 cgroup 里的 20 个残员**（`GetUnitProcesses` 逐条取证）：

- 17 个 Chrome 进程（2×crashpad、3×zygote、gpu、network/storage utility、9×renderer）——07:31 用户点外链时由壳 fork 出来；
- 2 个 codegraph MCP node（4417 `serve --mcp` + 4467 看门狗）；
- 1 个野 `cat`（6801）。

GNOME app scope 是 cgroup：主进程退出不让 unit 结束，**组内还有进程就一直是 active**（`pids.current=186`、`memory.peak=2.78 GB`、`memory.current` 缓涨），cgtop 因此长期显示「旧实例」。两个互相独立的成因：

1. **浏览器树落进壳的 cgroup**：`xdg-open` 由壳 fork；GLib 的 systemd-scope 启动只把浏览器**主 pid** 移进 `app-com.google.Chrome-6796.scope`（该 unit 只有 transient 头注释、无 `Description=`，与 gnome-shell 建的 2965/11839 不同形），Chrome 启动瞬间 fork 的 17 个 helper 早已继承壳的 cgroup 且永不被移走——浏览器活着，壳 scope 就永不空。
2. **dsh 下游不可见**：MCP stdio 服务器由上游 `dsh-mcp-client` 经 `StdioClientTransport`（`@modelcontextprotocol/client/stdio`）**裸 spawn**，不经 `dsh-subprocess-local` 的 systemd scope，故 [dsh 沙箱子进程孤儿泄漏](2026-09-12-dsh-sandbox-child-orphan-leak.md) 的 scope 收割判据结构上看不见它们；同时上游 `scrubbedParentEnv()` 把子进程环境里的 `DSH_*` 与 `KEY|PASSWORD|SECRET|TOKEN` 形名一并剥掉——旧血统 token（`DSH_DESKTOP_SPAWN_TOKEN`）两条都命中，血统扫描同样看不见它们（该 ADR 已记录这一前提）。

定因证据（本机 .NET 行为探针，2026-09-20）：`Process.Kill(entireProcessTree: true)` 在**父进程仍活**时可靠击杀全部子进程（父下 2 个子进程 0 存活）→ 现场残留属「击杀时父链已断 / 识别面不可见」一类，不是整树击杀本身不可靠；故修的是可识别性，不是重写击杀。

## Decision

1. **外链打开由独立 transient scope 承载**（`SystemBrowser`）：Linux 上 PATH 里探到 `systemd-run` 即以 `systemd-run --user --scope --collect --quiet -- xdg-open <url>` 启动，浏览器**整棵树**从此生在独立 unit（unit 名取 systemd 默认 `run-*.scope`，不伪装 `app-*`，避免与壳实例在 cgtop 里混淆）；探不到（非 Linux / 无 systemd user 会话）回退直启 `xdg-open`。scope 形态快速失败（子进程已退出且退出码非 0，如 user manager 不可达）时就地回退直启一次——可用性优先于 cgroup 卫生。返回值语义随之收紧：已退出且退出码 0 记成功（交棒成功），仅未启动或非零退出算失败；失败时把退出码与 stderr 末行（有界等待，不阻塞点击路径）写进 host.log——「该形态为何不可用」必须可判读，而不是只留一句「失败」。
2. **血统标记改为跨上游清洗存活的名字**（`RuntimeLineage`）：token 从 `DSH_DESKTOP_SPAWN_TOKEN` 改为 `HARNESS_DESKTOP_LINEAGE`，并新增同族 home 标记 `HARNESS_DESKTOP_LINEAGE_HOME`（值为生效 home）。名字不带 `DSH_` 前缀、不含 `KEY`/`PASSWORD`/`SECRET`/`TOKEN` 子串——这是 `scrubbedParentEnv()` 的**唯一**存活条件（本机实测三者：`DSH_*` 被前缀剥、`*_TOKEN` 被凭据形剥、`HARNESS_DESKTOP_LINEAGE*` 存活）。两个变量由 `BuildStartPsi` 注入（插件体检探针与主 spawn 同一事实源），因而不止 dsh 本身，其全部后代（含裸 spawn 的 MCP stdio 服务器与看门狗）都带标记。**旧名保留只读回退**（`LegacyTokenEnv`，仅 `ReadToken` 用、写侧恒新名）：升级窗口内上一版留下的 `.dsh-pid` 记录只带旧名，只读新名会让它「复验不匹配」而被跳过，孤儿继续占住首选端口（端口漂移 + 上一会话选中态丢失）；血统扫描面**不**回退旧名——上一版的后代本来就不在判据覆盖内，扩大扫描面只为历史形态没有收益。退役条件：本改名发布后再跨一个 minor。
3. **血统分类新增「后代」类**（`LineageKind.RuntimeDescendant`）：候选带 home 标记、home 与本次生效 home 相同，且命令行既非本 profile 的 dsh 服务端也非市场 helper → 判为壳的血统后代，进既有收割面（`SelectResidue` → `HarvestLineageResidue`：冷启动全量 + 进程内重启后收敛），但**不进接力证据面**（`IsRelayEvidence` 对后代类恒 false）——否则一个活着的工具 runner 会把崩溃恢复白等到接力预算上限。保护边界沿用既有单一原点 `ProtectedByRuntime`（在管运行时自身/后代/祖先，父链不可证一律不杀）：冷启动 `trackedPid=null` 时无在管运行时，凡血统可证者皆残留；有在管运行时，其整棵子树免收割。归属凭据是**标记本身**：home 只有 `LineageHomeEnv` 一处来源（与 token 同注入点、同剥离面，故「有 token 者必有标记」），无标记即 `None`——终端里另起的 `dsh` 即便 home 相同、命令行形如本 profile 服务端，也不被判成我方血统（零误杀）。home 标记是跨实例判别的必需项（dev 实例与正式版共用主机、home 不同）。
4. **续任者子树保护对称化**（`PlanPortConflict`）：收养市场接力续任者时，收割面除「不含续任者自身及其祖先」外还须排除**其后代**（`HarvestKillsSuccessor` 改为与 `ProtectedByRuntime` 同判据、换参照系）——后代类残留把整棵子树纳入收割面之后，续任者收养瞬间自拉的 MCP 服务也必须在「不得动」面内。
5. **退出编排与单实例锁释放时序维持现状**（有意不改）：`OrderlyQuit` 里 `disposeListener()` 先于关窗/看门狗，是[单实例启动器](../architecture/2026-08-26-single-instance-launcher-activation.md)的有意取舍——锁若留到进程终止才放，退出窗口内的重启会 bind 失败 → 通知无应答（2s 超时）→ 二启按设计静默退出，用户看到「点了图标没反应」。症状里的 4 秒双 scope 并存本身无害（旧实例只回收自己的在管 pid，marker 释放有 token 守卫），真正有害的残员由决策 2/3 在下次冷启动收敛。

## Alternatives considered

- **把单实例锁释放挪到进程终止前最后一步**：落败——见决策 5，会做出「重启静默无反应」窗口，是 UX 回归；残员的正解是让它可被识别收割。
- **退出时清扫「自身 cgroup 的全部成员」**：落败——壳 cgroup 里既有自己的进程，也可能有经激活令牌 / GLib scope 启动别的应用时留下的**他方**进程（本次 17 个 Chrome helper 即实例），「组内即我」不成立，误杀用户浏览器不可接受。
- **按命令行形状有界扫描 dsh 下游**：落败——[dsh 沙箱子进程孤儿泄漏](2026-09-12-dsh-sandbox-child-orphan-leak.md) 已拒（误杀面显著大于 scope 判据、会打死用户长任务）；本决策用自注入标记把归属做成可证，不继承该误杀面。
- **把 dsh 运行时装进壳自建的 systemd scope（下游随 scope 原子收割）**：落败——动 spawn 形态会改运行本身份契约（`.dsh-pid` 记的会是 systemd-run 的 pid、`Classify` 的 `--profile <desktop>` 命令行判据失效、收养与端口交接链跟着变），blast radius 远超本症状；决策 2/3 用标记达成同一收割面而不动身份契约。
- **给下游注入标记改为上游行为（让 `scrubbedParentEnv` 放行我方白名单）**：落败——上游事项（用户 2026-09-20 拍板不跟进），且壳侧选一个不被剥的名字即可达同一效果。
- **只做决策 1（浏览器侧）**：落败——MCP/helper 那 3 个残员在退出 5 分钟后仍活（CPU 近空转、内存缓涨），会随每次「用 MCP 的会话 + 退出」累积，且与 scope 收割判据的补集关系不清。
- **继承现状（只记录）**：落败——实机残留已到 1.5 GB / 20 进程量级，每轮「点外链 + 退出」都复现。

## Consequences

- 收益：退出后壳的 app scope 能真正空掉（浏览器树不再计入），cgtop 不再出现「旧实例仍活」；dsh 裸 spawn 的下游（MCP stdio 服务器、看门狗、工具 runner、野 helper）在下次冷启动或下次收敛被识别收割，重复 MCP 实例持 DB 句柄的形态随之消除。
- 代价：标记名成为**跨上游清洗的契约**——名字里出现 `DSH_` 前缀或 `KEY`/`PASSWORD`/`SECRET`/`TOKEN` 子串即静默失效（后代不可见，退化为只认服务端与 helper，方向安全但漏收）；该约束由 `RuntimeLineage.TokenEnv` / `LineageHomeEnv` 的 XML doc 钉住。
- 代价：冷启动收敛面扩到自己 spawn 链的全部后代——含用户经 dsh 起的后台长任务；与既有 `dsh-subprocess-*` scope 收割语义一致（下游随会话结束），但「跨重启续跑的后台任务」不再可能。
- 代价：旧名只读回退随本次发布保留一个升级窗口（退役条件见决策 2）——期间 `ReadToken` 对新旧两名字形都认，之后可删。
- 边界：插件体检探针以 **staging home** 起（`BuildStartPsi(homeOverride)`），而收割按生效 `ResolveDshHome()` 比对 home → 探针泄漏树不进收割面；探针自身 `finally` 整树击杀是它的兜底，壳在探针运行中崩溃才会留活树（既有限制，非本决策引入）。
- 边界：Chrome 一类被他方 fork 进壳 cgroup 的进程不带标记（标记只进 dsh 的 spawn 环境，不进壳自身环境）→ 永不被标记收割，靠决策 1 从根上不再进入壳 cgroup。
- 边界：非 Linux 无 `systemd-run`，决策 1 回退现状（Windows/macOS 本无 GNOME app scope 形态）；非 Linux 也没有 `/proc` 读取面，`Enumerate` 恒空，决策 2/3 退化回现状。
- 上游边界：MCP stdio 裸 spawn 仍在上游；若上游把下游统一收敛进 scope，决策 2/3 变冗余但无害（scope 收割判据自动覆盖）。

## Testing

- `SystemBrowserTests`（4 用例）：Linux 无 launcher 的 xdg-open 形态；有 launcher 的 scope 参数形态（`--user --scope --collect --quiet -- xdg-open <url>` 逐项断言）；非 Linux `UseShellExecute` 形态；`FindOnPath` 段序取首命中 / 空段跳过 / 无 PATH 与未命中。`Open` 的 spawn 与 stderr 尾行诊断是真机边界（沙箱无 systemd user 会话），实机验收覆盖。
- `RuntimeLineageTests`（+5 用例）：被清洗的后代（只剩 home 标记）判 `RuntimeDescendant`；标记 home 不一致判 `None`（跨实例零误杀）；无标记即便命令行形如本 profile 服务端也判 `None`（归属凭据是标记）；冷启动（无在管运行时）把后代类一并纳入残留；`PlanPortConflict` 收养时续任者后代不进收割面；`IsRelayEvidence` 对后代类恒 false。
- `RuntimeLineageProbeTests`（+1 用例）：`ReadTokenFromEnviron` 新名优先、空值折算为不存在并回退旧名、两处都无则 null。
- `HarnessRuntimeHostTests`（+1 用例 + 夹具跟值）：活的工具 runner 后代不把接力窗口拖满（宽限窗内回落）；假接力进程夹具改为带 home 标记（与生产「有 token 者必有标记」同形）。
- `SpawnEnvironmentHygieneTests` / `EnvironmentHygieneTests`：spawn 同时注入 token、home 标记与 `DSH_HOME`，前两者不进继承噪声剥离面。
- 全量 `dotnet test -c Release`：704/704 通过、0 警告（本机 Release 树运行值；README 徽章与本文件的全量计数随 CI 跟值批对齐）。
- 实机验收（待用户）：退出应用后 cgtop 不再出现旧 scope；下次冷启动 host.log 出现「收割桌面运行时残留（冷启动）：pid … kind=RuntimeDescendant」；升级窗口内旧版孤儿仍被 `.dsh-pid` 复验命中。

## Related

- [dsh 沙箱子进程孤儿泄漏](2026-09-12-dsh-sandbox-child-orphan-leak.md)：scope 收割判据（覆盖被 scope 包裹的下游）；本篇补其「裸 spawn 下游不可见」的补集，并解除其「token 被剥 ⇒ 血统扫描看不到」这一前提。
- [自更新退出路径确定性收割 dsh 子进程](2026-08-28-self-update-exit-reaps-dsh-child.md)：血统 token 的出处与 `.dsh-pid` 复验清扫；本篇把该 token 改名并把收割面扩到后代。
- [运行时交接收养](2026-09-12-runtime-handoff-adoption.md)、[市场接力收养优先](../feature/2026-09-15-market-restart-adopt-first.md)：`SelectResidue` / `PlanPortConflict` 的消费方。
- [在系统浏览器中打开外链](2026-08-21-open-external-links-in-system-browser.md)：`SystemBrowser` 的出处决策，本篇收紧其 Linux 启动形态。
- [单实例启动器激活](../architecture/2026-08-26-single-instance-launcher-activation.md)：锁释放时序的权衡依据（决策 5）。
- [spawn 环境净化与插件 spec 加固](../architecture/2026-09-09-spawn-env-and-plugin-spec-hardening.md)：`EnvironmentHygiene` 的剥离面，与上游 `scrubbedParentEnv()` 的边界。
