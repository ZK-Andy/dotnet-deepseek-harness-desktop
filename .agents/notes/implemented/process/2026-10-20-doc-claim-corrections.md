# Agent Note: 文档口径纠偏（README 六类七处 + docs 五处）

Status: implemented
Erratum: 2026-10-21 — 「点「检查更新」静默无动作」的判据失效：dev 下托盘菜单不出现该项、设置页明确提示不可用（[delivery-surface-self-verification](../testing/2026-10-21-delivery-surface-self-verification.md)）；当时对其余六类的判断保留。

Review: FULL/2026-10-20/R1=ok R2=ok R3=ok

## Problem

审计发现**文档比代码严**：产品面文案与实际实现脱节，且同一处错误事实散在多个家。逐条核实（证据见 Testing）：

- `README.md`「NativeAOT 就绪」——发运形态是 JIT（`PublishAot=false`），读者会读成「发的是 AOT」。
- `README.md`「单实例仲裁」未标平台限制——`LauncherActivation.ResolveInstanceSocketPath` 在 Windows 直接返 null（不启用互斥），而 Windows 是下载表里的受支持平台。
- `README.md`「崩溃 → 失败面」——实为连续崩溃达阈值（`RuntimeTimeouts.SupervisorFailureSurfaceStuckThreshold`，默认 3）才弹面。
- `README.md`「落后 `@next` 就更新」——实为「低于兼容底线即更新」的代理判据（不查 npm dist-tag）；同一文件两处（「首启说明」与「online-first 首启」条目）都写了旧口径。
- `README.md`「插件市场更新后的自重启收养」——市场已不由壳安装（ADR `2026-10-17-cancel-firstboot-market-preinstall`）。
- `README.md`「保持最新」段未提 dev 形态——dev 运行**默认**不装载自更新栈（`DSH_DESKTOP_UPDATE_FORCE=1` 可显式装载），点「检查更新」静默无动作。
- `docs/testing.md` 引用 `ConvenienceTests`——该测试文件不存在。
- `docs/architecture.md` 三处归属错误：把组合根称作「接线层」且漏了第二个 dot 分部（`Bootstrap/StartupSequence.Supervision.cs`）；`EnsureWorkspaceAllowBuilds` 挂在启动路径（实为市场安装侧写 `pnpm-workspace.yaml`）；称 `ryn.json` 含 `StartupWMClass`（实为只有 `identifier`，同值键在 `package-linux.sh` 生成的 `.desktop` 里）。

文档是事实源：错着比缺更贵——读者据此判断能力与平台支持。

## Decision

- `README.md` 六类七处按实现事实改写，`README.en.md` 逐条镜像同步（改写的是语义等价，不是逐字对应）。
- `docs/testing.md` 的死引用换成实际存在的测试类（`UpdateBannerTests`、`AutostartBuilderTests`）。
- `docs/architecture.md` 三处归属改正：组合根（`Program` + `DesktopAdapter*`，根集内唯一 dot 分部）与接线层（`Bootstrap/StartupSequence*`，另有 dot 分部）分开表述；`EnsureWorkspaceAllowBuilds` 归到市场安装侧；`ryn.json` 写明「无 `StartupWMClass`」（顶层三键 `identifier`/`bundle`/`capabilities`），同值键指到 `.desktop` 生成处。
- `docs/development.md` 同一处 `ryn.json`/`StartupWMClass` 错误一并改正（同一事实的第二家，不改则事实仍错）。

## Alternatives considered

- **维持现状不修**（文案与实现脱节照旧）：落败——文档是事实源，错着比缺更贵：读者据 README 判断能力（AOT）与平台支持（单实例），据此做错决策；而「文档比代码严」意味着缺口在文档侧，最便宜的修法就是改文档。
- **改代码去迁就文档**（开 AOT、给 Windows 上单实例、把崩溃阈值改成 1）：落败——那是产品决定，不是文档纠偏；本批只让文档说真话，行为不动。
- **只改 README、不改 `docs/`**：落败——`ryn.json` 那处错误在 `docs/development.md` 里也有，只改一处等于把同一错误留成两处。
- **删掉「NativeAOT 就绪」而不改写**：落败——读者会以为项目明确否掉 AOT；准确表述是「发布形态为 JIT（NativeAOT 未启用）」。
- **把平台限制写进下载表而不写功能条目**：落败——单实例与自更新的行为差异属于能力条目，读者看功能清单时就要知道。

## Consequences

- README 双语对实现的描述回到可证状态：NativeAOT、单实例平台面、崩溃阈值、更新判据（首启说明 + 功能条目两处）、市场与插件措辞、dev 形态自更新六类七处与代码一致。
- 文档归属改正后，「组合根」与「接线层」不再混用——这正是语义闸 `verify-compose-root.py` 区分的两面（根集管重量、装配面管方向）。
- README 徽章（tests/coverage）未动，测试数与覆盖率基线不变（1050/1050、64.77%）。
- 措辞只描述当前状态，不写变更历史；被替代的旧说法不留在文档里。

## Testing

- 逐条比对实现：`DesktopUpdateCommandRouter`/`UpdateRegistration`（dev 门禁默认不注册路由）、`LauncherActivation.ResolveInstanceSocketPath`（Windows 返 null）、`RuntimeSupervision`（`streak < FailureSurfaceStuckThreshold` 即不弹面，阈值默认 3）、`FirstBootBootstrapService.Resolve`（`RuntimeVersionGate.IsBelowFloor` 判据）、`MarketInstallHelper.Json.EnsureWorkspaceAllowBuilds`（写 `pnpm-workspace.yaml` 的 `allowBuilds` 6 项）、`src/DeepSeek.Harness.Desktop/ryn.json`（无 `StartupWMClass`）、`scripts/package-linux.sh`（生成 `.desktop` 带 `StartupWMClass`）。
- 「插件更新后的自重启收养」一条的机制面：`Infrastructure/Runtime/HarnessRuntimeHost.Handoff.cs` 的续任者搭乘段（观察市场自重启 helper 拉起的续任者）+ `Core/RuntimeLineage.cs` 的 `PortConflictAction.Adopt`（端口冲突时收养续任者）。
- 文档门禁全绿：`verify-md-links`（含新增 ADR 的相对链接）、`verify-doc-budgets`（`docs/architecture.md` 仍 ≤950 词上限）、`verify-readme-badges`（README 双语徽章 == `scripts/test-baseline.json`）。

## Related

- [publish-aot-jit-alignment](../simplification/2026-08-28-publish-aot-jit-alignment.md)——「发布形态为 JIT、NativeAOT 未启用」的事实家。
- [single-instance-launcher-activation](../architecture/2026-08-26-single-instance-launcher-activation.md)——单实例仲裁与「Windows 不启用」的平台边界（Decision 4）。
- [dsh-follow-next-channel](../architecture/2026-09-29-dsh-follow-next-channel.md)——「低于兼容底线即更新」的代理判据（不查 npm dist-tag）。
- [failure-surface-dialog-defects](../bug-fix/2026-10-15-failure-surface-dialog-defects.md)——失败面与崩溃阈值的现状。
- [cancel-firstboot-market-preinstall](../architecture/2026-10-17-cancel-firstboot-market-preinstall.md)——市场不由壳安装，本篇改写「插件市场更新后的收养」措辞的依据。
