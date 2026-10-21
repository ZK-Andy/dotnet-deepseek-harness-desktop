# Agent Note: 品牌图标资产删除

Status: implemented

Erratum: 2026-10-22 — 架构图已退役：正文「`assets/` 剩下的五个顶层条目均有真实读者…`architecture.svg`」与「README 媒体引用只有 `icon.png`/`screenshot.png`/`architecture.svg`」已失真——该资产已删，`assets/` 顶层余四项（`icon.ico`/`icon.png`/`icons/`/`screenshot.png`），见 [retire-architecture-diagram](2026-10-22-retire-architecture-diagram.md)；本篇保留删资产决策。正文与决定不动。

Review: LIGHT/2026-10-20/R2=ok（首轮 1 Blocker + 2 Suggestion：口径被自述证伪、计数错、注释漏说耦合；验轮 0 Blocker / 0 Suggestion 闭合）

## Problem

`assets/brand-icon.png`（102 KB）在仓库里是死物：没有任何代码、打包脚本、工作流或文档读它。

- 删除前的引用扫描：只命中 `scripts/package-linux.sh` 的一句注释（提到「brand-icon」这个名字），其余全仓零引用。Linux 打包实际安装的图标是 `assets/icons/<size>/apps.png`（hicolor 16–1024 多尺寸）与 `assets/icon.png`（pixmaps 主图标），两份都在。
- 它与 `assets/icon.png`（11 KB，同为品牌图标）观感重复，留着会被读成「打包用的品牌图标」，而打包面根本不读它。
- 2026-10-20 的功能闭环与架构一致性审计把它判为待拍板项：零引用资产，删留由用户决定，品牌资产不替用户判断。

## Decision

- 删 `assets/brand-icon.png`（`git rm`）。
- 改 `scripts/package-linux.sh` 的图标段注释：原文写「对齐 pilot-harness assets/icon.png → hicolor 512 + brand-icon」，现写成两个真实来源（`assets/icons/*/apps.png` 与 `assets/icon.png`）——被删资产的名字不再出现在打包与运行路径上（本篇自述除外）。

## Alternatives considered

- **保留文件当品牌素材备查**：落败——用户 2026-10-20 拍板删除；且这份备查的成本是每个克隆多 102 KB，外加一个会被误读为「在用」的文件。
- **把它接到打包面（替代 `assets/icon.png` 作 pixmaps 主图标）**：落败——零引用正是因为两个真实来源已覆盖各尺寸诉求（hicolor 多尺寸 + pixmaps 主图标）；再插一份同观感的来源只会多一处要同步的重复资产，图标规格仍由 `assets/icons/` 与 `assets/icon.png` 承载。
- **只删文件、不改脚本注释**：落败——删完注释就指向一个不存在的资产名，留一句假话；改注释是同一次删除的收口，不算越界改动。

## Consequences

- 仓库少一份零引用资产（−102 KB）。`assets/` 剩下的五个顶层条目均有真实读者：`icon.png`（Linux pixmaps 主图标、README 徽标、载荷冒烟解码对象）、`icon.ico`（Windows 安装器图标与 `ryn.json` bundle icon）、`icons/`（hicolor 多尺寸）、`architecture.svg`（README 架构图）、`screenshot.png`（README 界面截图）。
- 打包行为零变化：删的是未被任何构建路径读取的资产，改的是不参与执行的注释。
- 品牌素材日后若要新增，落在 `assets/icons/`（hicolor 尺寸集）或 `assets/icon.png`（主图标），不再新增「平行品牌图标」这一位置。

## Testing

- 删除前后各做一次全仓引用扫描（排除 `.git`/`.dotnet-cache`/`bin`/`obj`/`node_modules`）：删除后 `git grep -i brand-icon` 的命中全部落在本篇自述（Problem/Decision/Testing 三处共 5 行），仓库内无其他命中——按本仓惯例显式披露残留文件名（与姊妹篇 `2026-10-20-dead-asset-cleanup` 同做法）。
- 删除前的扫描结论：`.csproj` 只有显式 `<Content Include="..\..\assets\icon.png">`（无 `assets/**` 通配）；`scripts/`、`packaging/`、`.github/` 无整目录拷贝；README 媒体引用只有 `icon.png`/`screenshot.png`/`architecture.svg`；`ci.yml` paths-filter 只列 `icon.png`/`icon.ico`；`verify-package-layout.sh` 无图标断言；Inno Setup 模板不引用被删资产。
- `scripts/package-linux.sh` 仅注释改动：`bash scripts/package-linux.sh --self-test` 与 `bash scripts/verify-shell-standards.sh` 全绿。
- 本次变更面 = 一份二进制资产 + 图标段注释改写（两行），无 `src/`/`tests/` 改动，故不跑 `dotnet build`/`dotnet test`（按 diff 面选最窄检查）。

## Related

- [dead-asset-cleanup](2026-10-20-dead-asset-cleanup.md)——同一批审计的其余死物清理（`assets/desktop.patch.yml`、`templates/agents-hierarchy.md`、`scripts/setup-hooks.sh`、死常量）。
