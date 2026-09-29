# Agent Note: dsh 跟版通道切 next

Status: implemented

Review: FULL/2026-09-29/R1=ok R2=ok R3=ok

## Problem

上游 dsh 的活跃预发布线移到 `next` 标签（registry 实测：`next = 0.2.0-rc.2`，`alpha = 0.1.7-alpha.2`，`latest = 0.1.7-rc.2`）。桌面首启引导的 `DshSpec` 缺省仍为 `@deepseek-ai/dsh@alpha`（`RuntimeBootstrapOptions.DshSpec` 代码缺省 + `appsettings.json` 同值），继续跟 `alpha` 即跟住一条不再更新的线：新机装到旧内核，与"简单壳跟随最新预发布"的契约相悖。

## Decision

跟版通道改 `alpha` → `next`，不钉版：

1. **缺省值**：`RuntimeBootstrapOptions.DshSpec` 缺省与 `appsettings.json` 的 `DshSpec` 同改为 `@deepseek-ai/dsh@next`。用户显式配置仍优先（`Load` 覆盖语义不变）。
2. **文案**：`UiCopy.BootstrapInvalidDshSpec` 的 registry 示例与 `VersionFloorBannerText` 的通道指引（中英）同步改 `next`；根 README 双语三处（标语/正文/功能项）与 `docs/user-guide.md`、`docs/architecture.md` 的通道陈述同步改 `next`。
3. **不变量**：`RuntimeVersionGate.MinimumVersion`（`0.1.2-alpha.2`）不动——它是协议兼容底线，与跟版通道解耦；`next` 当前指向（`0.2.0-rc.2`）高于底线，引导的"落后即更新"判定无需改动。`release.yml` 的预发布 tag 正则（`rc|beta|alpha`）判的是桌面自身版本号，与 dsh 通道无关，不动。
4. **历史笔记不动**：已落地的 implemented 笔记中对"跟随 `@alpha`"的叙述是当时的决定（Erratum 纪律：正文历史叙述不改写），本篇为该通道决定的现行收敛点；`simple-shell-single-global-dsh` 的通道结论被本篇部分取代，其余（简单壳形态、单全局、sudo 指引）继续有效。

## Alternatives considered

- **继续跟 `alpha`**：落败——`alpha` 标签停在 `0.1.7-alpha.2`，上游更新走 `next`，跟住即主动落后。
- **跟 `latest`**：落败——`latest` 为 `0.1.7-rc.2`，同样滞后于 `next` 的 `0.2.0-rc.2`，且 simple-shell-single-global-dsh 已否决 `latest`（滞后）。
- **钉版 `0.2.0-rc.2`**：落败——回到"桌面与用户版本可分叉"，违背 online-first 内核升级与壳发版解耦契约；`upgrade-ryn-and-dsh-runtime` 与 `desktop-profile-rename` 两篇已先后否决钉版。
- **改缺省但留文案/README 在 `alpha`**：落败——同一事实多家陈述，事实单一家纪律要求同批跟值。

## Consequences

收益：新机与落后机的首启引导装到上游当前活跃线（`0.2.0-rc.2` 起），与 CLI/TUI/Web 的"同一宇宙"回到同一版本线。代价与风险：`0.2.x` 为 minor 跨线，首启即吃上游 breaking 的概率高于同线跟随；兜底为 `MinimumVersion` 底线横幅（只提示不阻断）与引导重试页，本批不加新机制。`architecture.svg/html` 生成物中的 `PATH @alpha` 标注随既有"架构图重生成"挂账走（本沙箱无 archify profile），不手改生成物。

## Testing

`RuntimeBootstrapTests`（缺省/覆盖/损坏回退断言改 `@next`，`InstallArgs` 钉改名 `ContainGlobalAndNextSpec`）+ `SpawnEnvironmentHygieneTests` / `NpmPrefixExposeTests` / `MarketInstallHelperSpecTests` 的 spec 字面量同步；全量 `dotnet test` 与 `verify-readme-badges`（基线计数不变）绿。

## Related

- ADR simple-shell-single-global-dsh（`implemented/architecture/2026-08-31-simple-shell-single-global-dsh.md`）：本篇部分取代其"跟随 `@alpha` 通道"一点，其余有效。
- ADR pnpm-caret-spec-rejection（`DshSpec` registry 形态契约与 fail loud 语义，本批未动）。
- ADR upgrade-ryn-and-dsh-runtime / desktop-profile-rename（钉版已两度被否决，本批维持不钉版）。
