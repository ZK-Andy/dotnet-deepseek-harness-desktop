# Agent Note: 随包插件出货面改动必须 bump 版本（机械闸）

Status: implemented

Review: FULL/2026-10-29/R1=ok R2=ok R3=ok

## Problem

随包插件的换入判据只看版本号。`BundledPluginCatalog.AssemblePending` 对每个清单项执行「未装即装、来源版本更新即升、同版或更高跳过」，比较只发生在 `PluginVersionCheck.NeedsUpgrade(installedVersion, bundledVersion)`，即 `UpdateVersion.Compare(bundled, installed) > 0`。这条判定把 `package.json` 的 `version` 变成了**唯一的换入触发器**：插件内容改了而版本不动，已装用户的 profile 里的旧副本会永久留着，且全程零报错、零日志异常（每次都打印「随包插件无需安装（companion 已就位），跳过」）。

2026-10-28 的八语言化批次（`4ed15ba`）正是这个形态：`plugins/dsh-desktop-companion/client/client.js` 从两本字典扩到八本、`ctx.locale.register(NS, {zh, en, ja, …})`，而 `package.json` 版本仍是 `0.0.23`（该值由 `e5ff286`（2026-10-24，G5 收尾）从 0.0.22 升上来，之后未经任何 bump；更早的 `2dfbb57`（2026-10-08）是 0.0.21 → 0.0.22）。装 v0.7.10 后单机实测【探索性 · n=1，实验设置：本机 profile + 安装器自带 tgz，2026-10-29】：tgz 内是八本字典的新产物（895 行，含 `var ja`），而 profile 里`node_modules/dsh-desktop-companion/client/client.js` 是旧副本（853 行，`register(NS, { zh: zh, en: en })`，零 `var ja`）；`host.log` 每次启动都是「已就位，跳过」。两处行数可从 git 树复核（v0.7.10 = 895、v0.7.9 = 853），profile 侧副本与日志属单机观察。该观察是本批动因，不承担任何后续决策前提。用户看到的现象因此是「切了日语，桌面设置页还是中文」——dsh 自身的语言切换器（`dsh-client-locale` 内置 ja/es/fr/de/ru/pt 语言包）工作正常，失效的是宿主壳那半边。

**这是纪律失效而非设计缺陷**：ADR `2026-08-25-bundled-plugin-version-aware-catalog` 已经写下「改插件必须 bump version」并扩到清单全体，`2026-10-24-avalonia-g5-ryn-host-retirement` 的评审（S4）也抓过一次同类漏（0.0.22 → 0.0.23）。但该纪律只存在于文字里，没有任何门禁核对「出货面改了而版本没动」——它恰好落在「机器能查而未接门禁」的缝里，于是第二次发生。

## Decision

新增门禁 `scripts/verify-bundled-plugin-version.py`，把「改包内容必须 bump 版本」变成可执行判据。

1. **判据**：某随包插件本次变更触及**出货面**（`scripts/build-companion-tgz.sh` 打进 tgz 的路径：`package.json`、`cordis.patch.yml`、`lib/`、`client/`）时，该插件 `package.json` 的 `version` 必须相对 base **严格变大**。降版同样判红——`NeedsUpgrade` 只在「来源更新」时换入，降版等于永装不上。`test/`、`testkit/`、README 改动不要求 bump（它们不进 tgz）。
2. **清单不另立**：随包插件名单从装配侧唯一事实源 `BundledPluginSupply.All` 解析，闸不维护第二份名单。出货面**不另立常量**：每次运行从 `build-companion-tgz.sh` 的 `cp ... "$SRC/<x>"` 行现解析，脚本增删出货路径即自动进/出守卫面。代价明示：失去了「出货面变化须人工过一眼」的绊线——脚本改成非 `$SRC/<x>` 形态、或某个 cp 源解析不到，闸会判红而不是静默缩小面。
3. **出货面必须覆盖每个成员**：`shipped_paths` 解析出的是**全局一份**出货面。某清单成员若本次有改动、而改动全部落在该出货面之外，按「出货面不可判定」判红——否则新增的清单成员会完全脱离判据（静默 `continue`）。三个豁免名与判据 B 同一组：`test/`、`testkit/`、`README.md`（都不进 tgz）。局限见 Consequences：闸分不清「本成员的出货面」与「别人的出货面」。
4. **不可判定即判红**：`--since` 的 base 不可达、`BundledPluginSupply` 或 tgz 脚本解析不出内容、版本串读不出或不可解析——一律违规而非放行（沿用 ADR `review-tier-escape-proofing` 的先例：静默放行的门等于没有门）。**唯一豁免**：`package.json` 在 base 侧不存在，即该插件是本次新增的——无旧版本可比，递增规则不适用。该豁免靠三态读取（不存在/读失败/有内容）与「读失败」区分开，不允许把 git 故障当成「新插件」。
5. **三档执行**：pre-commit `--staged`（index vs HEAD）、pre-push 与 CI `--since <base>`（pre-push 取 `merge-base origin/main HEAD`，与增量纪律闸同款）、默认工作树。**CI 的一处例外**：`DSH_REVIEW_BASE_STATE=none`（首推/新建分支，无可比 base）时本闸只告警跳过——此时无从比较版本，跳过而非硬红，与增量纪律闸同口径；同文件里评审档位闸对同一状态硬红，是因为它在无 base 时可退化成恒绿（工作树扫描在干净 checkout 上是空集）。本闸跳过不留下永久缺口：该提交在之后任意有 base 的评审区间里会被同一判据重新覆盖（判据比的是 base 侧版本与 HEAD 侧版本，`origin/main` 一建立即补抓）。`--self-test` 用临时 git 仓库走真实 git 路径（含 staged 时刻的 `:path` 读法），共 25 条断言：出货面改动而版本不动、同批带 bump、降版、仅 `test/` 改动、`cordis.patch.yml` 计入出货面、出货面文件**重命名移出**、`--since` 档的判红与放行（2）、`--target-directory` 形态的真源不丢（1）、`cp -a`/续行/尾斜杠/行尾注释的出货面解析、cp 源解析不到（2）、不可判定 base、出货面未覆盖的改动判红；版本语义 9 条（数字段而非字典序、段数不等、`v` 前缀、`-` 后缀、三例不可解析抛 `BadVersion`）；`version_of` 2 条（顶层键、非 JSON）。

本条与既有决定的关系：`BundledPluginCatalog`/`PluginVersionCheck` 的换入语义不变（唯一家仍是那两处）；本闸只保证**版本号如实反映内容**，不改变判定方向。

## Alternatives considered

- **在 `BundledPluginCatalog` 里改成按内容哈希判定换入**（不靠版本号）：落败——`pnpm`/`dsh plugin add` 的装配链以版本号为准入语言，内容哈希要自建旁路清单，profile 是可再生状态，旁路清单一丢就退化成重装；且「改了内容就 bump 版本」本就是 npm 生态的通行契约，绕过它会让 `pnpm-lock` 与已装副本对不上。
- **每次启动都强制重装 companion**（无视版本）：落败——把一个每次启动都跑的 `pnpm add` 装到热路径上，冷启动 5–7s 稳定化之外再叠一段网络/磁盘不确定性；且与 `plugin add` 裸名对既有依赖是 no-op 的实测（cookbook「pnpm 裸名 add 对既有依赖是幂等 no-op」）相撞，`file:` spec 不变时重装也不换内容。
- **tgz 内容哈希比对兜底 bump 遗漏**（ADR `2026-08-22-companion-plugin-version-aware-upgrade` 已记该路线落败但注明「可与版本号并存」）：本批未采纳为**主判据**，因为它要求自建旁路哈希状态，而 profile 是可再生状态、旁路一丢即退化成重装；但这确实能盖住本闸「版本变了内容没变」与「内容变了塞进旧版本号」两个未覆盖面。留作后续项，不在本批做。
- **只写进评审检查项（AI 兜底）**：落败——已有两次人情漏网（2026-10-24 S4、2026-10-28 本次），且判据完全机械（路径前缀 + 版本串比较），进机器门禁的成本远低于第三次漏网的事后排查。
- **把判据塞进 `verify-code-conventions.py`**：落败——该闸的判定面是 C# 注释感知扫描（D004/D005），塞进「插件 JS 版本」会让一个闸承担两个正交判据，红时命名不再指向原因；单开一个薄闸更符合现有 `verify-host-command-ids.py`/`verify-core-purity.py` 的一闸一判据形态。

## Consequences

- 收益：出货面改动而无版本递增的形态在提交面即被拦；2026-10-28 的事故形态可被复现（在 `4ed15ba^..4ed15ba` 上跑本闸，输出正是「版本未升 0.0.23 → 0.0.23」）。
- 代价：新增一道门（pre-commit + CI docs job 各一次，纯文本扫描，毫秒级）；每次改 companion 出货面都要手工 bump，漏了会被拦下来（这正是目的）。
- **未覆盖面**：本闸只判「版本变了」，判不了「版本变了但内容没变」的空 bump；也判不了「内容变了却把旧版本号原样留着」的形态（后者即本轮事故本身，已由判据 B 挡住）。版本解析还有四处与 `int.TryParse` 不等价（Unicode Nd 数字、int32 溢出两处假绿；`+1.0` 一处误红；`²` 一处裸 `ValueError`）——触发前提是把随包版本号写成非 ASCII 数字或31 位整数，属人为构造而非真实失效形态，**留作后续项**（修法：`re.fullmatch(r"[0-9]+")` + 2^31 上限）。
- **已发出去的 v0.7.10 无法修**：存量装机用户的 profile 里仍是旧 companion，本闸对已发版本零效力；唯一的修复路径是下一次发版带上 `0.0.24`，让 `NeedsUpgrade` 判「来源更新」换入。这也是本 ADR 落地的同时必须 bump 当前版本的原因。
- 组装面事实：随包插件清单成员仍由 `BundledPluginSupply` 逐案登记。判据随成员数扩展的**前提**是所有成员共用同一份出货面（同一个 `build-companion-tgz.sh`）：`shipped_paths` 解析出的是**全局**一份。某成员有改动而改动落在该出货面之外（`test/`、`testkit/`、README 除外）时，判据按「出货面不可判定」判红，不会静默放过；但闸分不清「该成员的出货面」与「别人的出货面」——成员各自的打包脚本若不同，需先把出货面按成员参数化，属后续项。

## Testing

- `python3 scripts/verify-bundled-plugin-version.py --self-test`：25 条断言，见 Decision 第 5 条。其中两条是为评审发现的假绿补的：重命名折叠（R2 B2）、版本串不可解析（R2 B1）——两者此前零覆盖。
- 真实回放：在 `4ed15ba` 的 worktree 上执行 `--since 4ed15ba^`，输出 `plugins/dsh-desktop-companion/package.json: 出货面已改（…client/client.js）但版本未升 0.0.23 → 0.0.23`——判据对历史事故形态成立。
- 本批同时把 companion 版本 bump 到 `0.0.24`，故本批自身过闸（出货面改了且版本递增）。
- 接线点：`.githooks/pre-commit`（`--staged --enforce`）、`.githooks/pre-push`（`--since <merge-base origin/main> --enforce`，与增量纪律闸同款）、`.github/workflows/ci.yml` docs job（`--self-test` + `--since` + `--enforce`）。

## Related

- ADR `implemented/feature/2026-08-25-bundled-plugin-version-aware-catalog.md`：版本感知换入语义的单一事实源（未装即装/更新即升/同版跳过），本篇在其上补机械判据。
- ADR `implemented/architecture/2026-10-24-avalonia-g5-ryn-host-retirement.md`：同形态漏网第一次（评审 S4 抓到 0.0.22 → 0.0.23）。
- ADR `implemented/feature/2026-10-28-ui-copy-eight-locales.md`：八语言化本身；其 Consequences 未预见「插件版本未 bump 会让 companion 那半边对存量用户失效」。
- ADR `implemented/process/2026-09-03-review-tier-escape-proofing.md`：「不可判定 diff 一律判红」的先例。
