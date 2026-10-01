# Agent Note: Windows 冒烟腿 runner 前置——WebView2 先探后装与 npm 缓存键跟版

Status: implemented

Erratum: 2026-10-01 — 「Defender 实时防护逐文件扫描是首启装包慢的主因」这一依据被同批 dispatch（run 36831059163：该镜像 `(Get-MpPreference).DisableRealtimeMonitoring=True`、排除面本就含 `C:\` 与 `D:\`）证伪，随之撤回了 npm 路径排除及其判门；正文只写现存现实，被撤回方案记在 Alternatives 的「对 npm 相关路径加 Defender 实时防护排除」条。本篇其余决定（WebView2 先探后装、npm 缓存键跟版）不受影响。

Review: FULL/2026-10-01#2/R1=ok R2=ok R3=ok

## Problem

Windows 打包腿的时长由「安装冒烟」步决定，而该步的时长由首启引导里的
`npm install -g @deepseek-ai/dsh@next`（~500 包）决定。发版实测（n=6，源 = 各 run 的
step 时间戳与 `smoke-logs` artifact 内的引导日志）：

| release run | dsh 跟版线 | npm 包数 | 引导耗时 | 冒烟步 |
|---|---|---|---|---|
| 36511771285 (v0.5.9) | @alpha | — | 539s | 570s |
| 36563569782 (v0.5.10) | @alpha | 512 | 274s | 299s |
| 36620646853 (v0.5.11) | @next | 539 | 240s | 265s |
| 36743813721 (v0.5.12) | @next | 538 | 388s | 413s |
| 36768431870 (v0.5.13) | @next | 538 | 195s | 252s |
| 36822440100 (v0.5.14) | @next | 512 | 317s | 347s |

同批对照：run 36822440100 的 Linux 腿装同一 dsh 版本用 **44s**（538 包，该腿无 npm 缓存），
同一 run 的 Windows 腿装 **512 包**却用 317s——两条腿的包数差 26 来自平台相关的 optional
依赖，即 Windows 侧装的包更**少**、用时是 7 倍。Windows 腿的下载缓存反而命中并恢复了 189MB
（`Cache restored from key: npm-cache-windows-x64`）。故差距不来自下载——**Windows 侧是本地
落盘成本**（跨平台对照仅该 run 一次，Windows 六次引导耗时为 Linux 44s 的 4.4–12×
【探索性：对照 n=1】）。具体机制**仍未证**：Defender 逐文件扫描这一候选已被 runner 自身状态
排除（见 Status 下的 Erratum），Windows 无法像 Linux 那样从 cacache 硬链接而退化为整份拷贝是
另一候选【推断 · 未证】。最慢一次引导尝试 539s（该行计整条 `RuntimeBootstrap.RunAsync`，见
`FirstBootBootstrapService` 的成功分支；npm 单步 ≤ 该值），而冒烟等待窗 720s 正是从单步默认
`RuntimeBootstrapOptions.StepTimeoutMinutes=10`（=600s）推出来的——539s 与窗余量 181s、
与单步上限余量 ≥61s（按上界估，【推演】），再慢一档就红成「步骤超时」或「等待窗耗尽」，
红的是 runner 速度而不是产物。

同批发现的两处非成因失败（记录以免被误读为卡死）：`dsh plugin add` 因 runner 无 pnpm 而
exit=1（桌面只写转发 shim，三平台一致，冒烟按无人值守跳过，见 ADR
[pnpm-caret-spec-rejection](../testing/2026-09-25-pnpm-caret-spec-rejection.md)）；以及
[smoke-install-windows.sh](../../../../scripts/smoke-install-windows.sh) 的 EXIT trap 早前
把 `rm` 的 busy 失败透传成 exit 1、将绿 verdict 翻红（清理分支现以 0 返回）。

另外两项与本次同批处理的成本：WebView2 Evergreen 步每次无条件下载+静默安装（run 36822440100
该步 63s，06:01:20→06:02:23），而 runner 镜像自带 WebView2 运行时（版本旧于 Edge，见
[actions/runner-images#9538](https://github.com/actions/runner-images/issues/9538)）；
npm 缓存主键是常量 `npm-cache-windows-x64`，条目自 2026-09-27 建立后再未更新（来源：
`gh api repos/ZK-Andy/dotnet-deepseek-harness-desktop/actions/caches` 唯一一条，
created_at 2026-09-27T11:52Z / last_accessed_at 2026-10-01T06:01Z / 189MB），而跟版通道
（`@next`）是移动靶。

## Decision

三件，落在 CI 侧，不动产品代码：

**1｜Windows 冒烟腿前置收进一个脚本（唯一家）**：新增
[prepare-windows-smoke.sh](../../../../scripts/prepare-windows-smoke.sh)，由 `package.yml` 的
`build-windows` 腿在「安装冒烟」前调用；原先内联在 workflow 里的 WebView2 步随之删除。
脚本只做一件事，判门语义明确：

- **WebView2 先探后装（先探不判门，安装失败判红）**：以注册表 `pv` 探针（HKCU + HKLM 两处
  EdgeUpdate client 键；判据与仓内 [installer.iss.in](../../../../packaging/windows/installer.iss.in)
  的 `IsWebView2Available` 同源——`pv` 非空且非 `0.0.0.0`）判定运行库是否已登记；
  已登记即跳过 Evergreen 下载与 `/silent /install`，未登记才走原安装链（下载 3 次重试、
  以 curl 退出码而非文件存在性判定成败，失败 fail loud）。安装目录枚举与 `pv` 结论只留痕不判门。

脚本自带 `MSYS2_ARG_CONV_EXCL='*'`（`/silent /install` 的 Git Bash 路径转换，不再依赖调用方
env）、`--self-test`（`pv` 四态判定夹具）已接进 `ci.yml` 的自测步。

**2｜npm 缓存键跟版**：`package.yml` 新增一步，在缓存步之前解析跟版线当前版本——规格读
`appsettings.json` 的 `RuntimeBootstrap.DshSpec`（通道名的单一来源，不在工作流里复写），再
`npm view <spec> version`，组键 `npm-cache-windows-x64-dsh-<version>`；`restore-keys` 用
`npm-cache-windows-x64` 单前缀（同前缀多命中取最近创建者，故它同时覆盖新版本条目与更早的
常量键条目），上游换版时退到最近条目并在 job 末存下新键（此处恢复 `restore-keys`：删除理由见
[ci-package-workflow-unification](2026-09-28-ci-package-workflow-unification.md) 的 Decision 三；
恢复理由见本篇 Alternatives 的「缓存键不做运行时解析」条）。解析失败**不判门**（打印
`::warning::`，键退化为常量后缀 `unknown`）：该次仍由 `restore-keys` 命中旧条目，但
`actions/cache` 在精确命中时不回存，故该键一旦建立便不再更新——即退化成「常量键→条目永久
冻结」形态，直到解析恢复；接受这一退化是因为解析失败意味着仓库/registry 侧真出了问题，
而缓存是优化，判红等于用一次发版换一个缓存键。

**3｜不缓存 `%APPDATA%\npm` 安装树**：维持既有决定——Windows 腿继续真实演练首启冷装路径
（cacache 按内容寻址，陈旧只降命中率，不会陈旧产物）。

## Alternatives considered

- **缓存 `%APPDATA%\npm` 全局安装树**：落败（本批不做）——直接消除首启装包成本，但 Windows 腿
  就不再演练冷装这条产品路径，本类回归（装包变慢/失败）会被缓存命中掩盖。首启装包本身在本批
  没有拿到提速手段（见 Consequences），故这是下一手。
- **对 npm 相关路径加 Defender 实时防护排除（曾作为本篇 Decision 落地，同批撤回）**：撤回——首个
  dispatch（run 36831059163）的留痕显示该镜像 `DisableRealtimeMonitoring=True` 且排除面本就含
  `C:\`、`D:\`，再加 `%LOCALAPPDATA%\npm-cache` / `%APPDATA%\npm` / `C:\Program Files\nodejs`
  三条是空操作；同 run 引导耗时 188s 落在改前分布（195–539s）内，也支持「无效果」。故该机制连同
  其判门一并删除：一个在目标 runner 上不产生效果的门，正是 ADR
  [ci-gate-honesty](2026-09-28-ci-gate-honesty.md) 所禁止的形态。更钝的
  `Set-MpPreference -DisableRealtimeMonitoring $true` 同样不取（整机失防护，且该镜像本就关闭）。
- **直接删掉 Evergreen 步（不先探）**：落败——镜像自带运行时的版本可能旧于 Edge，一旦冒烟从
  全链退化为安装链，本腿就失去「起得来」覆盖；先探后装在命中时省 60s，未命中时保留原兜底。
- **`hashFiles` 组缓存键**：落败——通道是移动靶，`appsettings.json` 内容不变而 registry 指向已变，
  hash 得到的是一个假稳定键。
- **缓存键不做运行时解析（常量键 + `restore-keys`）**：落败——常量键让条目永久冻结在首次保存
  的闭包，跟版通道换包后旧条目只降不升。（此前
  [ci-package-workflow-unification](2026-09-28-ci-package-workflow-unification.md) 依「主键恒为
  常量、前缀回退恒不参与」删掉了 `restore-keys`；键跟版后前缀回退才真正参与，本批随之恢复。）
- **把缓存键解析失败判红**：落败——见 Decision 第 2 条的判门语义分离。
- **冒烟腿整体移出发版门（只留 dispatch）**：落败——`release.yml` 以 `needs:` 让发布作业等包腿
  是 [ci-package-workflow-unification](2026-09-28-ci-package-workflow-unification.md) 的既定
  契约（包腿红即不发版），把冒烟降级为非常规车道等于放弃发版前的「装得上、起得来」覆盖。

## Consequences

- 买到：镜像自带 WebView2 时该步（下载+静默安装）整步省掉（读数见 Testing）；npm 缓存条目不再
  永久冻结在建立时的闭包（跟版键每次换版重存，读数见 Testing）。
- 未买到：首启装包本身没被加速——Defender 排除撤回后这个成本项回到无手段状态；真要砍它只剩
  「缓存 `%APPDATA%\npm` 安装树」（首条 Alternatives，需先接受冷装覆盖的损失）。
- 代价：Windows 冒烟腿新增一处 runner 状态依赖（镜像是否自带 WebView2）；未自带时行为回落到
  本步引入前的 Evergreen 安装链（fail loud），不新增红面。

## Testing

- 本地：`bash scripts/prepare-windows-smoke.sh --self-test`（`pv` 四态）；`verify-shell-standards.sh`
  （27 文件 S1–S6）；`actionlint -shellcheck="shellcheck -S warning"`；`verify-governance.py`。
- CI（run 36831059163，`release.yml` 分支 ref dispatch）：`冒烟前置` 4s——该步读数含当时尚未撤回的
  Defender 排除段（约 3s），WebView2 探测段本身约 1s（对照改前该步的下载+静默安装 ≈63s）；
  `解析 dsh 跟版线版本` 7s（`@deepseek-ai/dsh@next` → `0.2.0-rc.2`）、缓存步 `Cache restored
  from key: npm-cache-windows-x64`（经前缀回退）→ job 末 `Cache saved with key:
  npm-cache-windows-x64-dsh-0.2.0-rc.2`、`安装冒烟` 217s（改前 252–570s）、win 腿 8m53s → 5m40s；
  同 run 引导 188s（538 包）落在改前 195–539s 内。`pv=153.0.4234.48` 命中行与
  `DisableRealtimeMonitoring=True` 留痕即本篇撤回 Defender 的依据。
