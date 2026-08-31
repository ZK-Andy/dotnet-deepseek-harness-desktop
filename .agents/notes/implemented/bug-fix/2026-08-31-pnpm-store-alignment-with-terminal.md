# Agent Note: pnpm-store-alignment-with-terminal

Status: implemented

## Problem

桌面壳 spawn 的 dsh 子进程被 `HarnessRuntimeHost.BuildStartPsi`（`ApplyPnpmWriteDirs`）注入了
`pnpm_config_store_dir=~/.dsh/.pnpm-store` 与 `pnpm_config_cache_dir=~/.dsh/.pnpm-cache`，因此
desktop profile（`~/.dsh/profiles/desktop`）的 `node_modules` 是用 `~/.dsh/.pnpm-store` 这个 store 链接出来的。

但用户在**终端**直接调 `dsh plugin --profile desktop add <pkg>` 时，`dsh plugin` 是上游
`@deepseek-ai/dsh` 的命令，经 `spawnSync("pnpm", args, { stdio: "inherit" })` 调 PATH 上的 pnpm，
**不注入任何环境变量**（该项目 cookbook 条目已实证：dsh plugin 经 `spawnSync("pnpm")` 从 PATH 调 pnpm，无捆绑）。

于是终端里的 pnpm 落到**默认 store** `~/.local/share/pnpm/store/v11`，而 profile 的 `node_modules`
是用 `~/.dsh/.pnpm-store` 链接的。pnpm 11 对比两者不一致即拒绝一切 add/remove：

```
[ERR_PNPM_UNEXPECTED_STORE] Unexpected store location
The dependencies at "…/profiles/desktop/node_modules" are currently linked from
the store at "…/.dsh/.pnpm-store/v11". pnpm now wants to use the store at "…/.local/share/pnpm/store/v11".
```

这是**桌面与终端行为不一致**的产品缺陷：同样一个 desktop profile，桌面里插件装得上，终端
`dsh plugin` 却必然报 `UNEXPECTED_STORE`，用户无法理解为何"一条命令的事"在终端做不成。

追加根因（2026-08-31 实测）：pnpm 11 对 `store-dir` 的解析只认**环境变量**与 `--store-dir` CLI flag，
**不读** `.npmrc`、`~/.config/pnpm/rc`、项目 `.npmrc` 里的 `store-dir`（三个隔离 HOME 实验全部被忽略，
仍解析到默认 `$HOME/.local/share/pnpm/store/v11`）。因此"把 store-dir 写进某个全局 pnpm 配置文件"这条路
在 pnpm 11 上**不可行**；唯一能被 pnpm 11 承认、且能让终端借由 shell 环境继承的通道是
**shell 启动文件里的 `export pnpm_config_store_dir=…`**（环境变量通道）。

## Decision

**桌面壳在自己已经会写的 shell rc 幂等块里，一并把 pnpm store/cache 目录导出，让终端与桌面共用同一份 `~/.dsh/.pnpm-store`。**

复用既有机制而非新造：`CliShimRegistrar`（ADR simple-shell-single-global-dsh）在启动时已把
`~/.local/bin`（以及系统全局 node bin）写进用户 shell rc 幂等块（`CliShimPath.BuildShellExportBlocks` +
`EnsureShellRcBlock`，只写已存在的 `.bashrc/.zshrc/.profile/...`，幂等、best-effort、绝不阻断启动）。
方案 B 即在此块内**追加**两行 pnpm 环境变量 export：

```sh
export pnpm_config_store_dir="$HOME/.dsh/.pnpm-store"
export pnpm_config_cache_dir="$HOME/.dsh/.pnpm-cache"
```

- 这样**终端里首次运行 `dsh plugin` 时**，用户 shell（bash/zsh）已从 rc 继承与 desktop 一致的 store-dir，
  pnpm 用同一个 store 解析，`UNEXPECTED_STORE` 消失，与桌面行为一致。
- 该块带 `# >>> deepseek-harness-desktop >>>` / `# <<< deepseek-harness-desktop <<<` 幂等标记，
  已是本应用生成的受控块，追加 pnpm export 不会污染用户配置；已含则零写入。
- store 目录若不存在仍由 `HarnessRuntimeHost.ApplyPnpmWriteDirs` 预建（`~/.dsh/.pnpm-store/.pnpm-cache`），
  幂等。
- 仅在操作系统非 Windows（Unix）侧生效——Windows 走 HKCU 注册表 PATH，本缺陷（store 解析）
  在 Windows 上由 pnpm 的 `%LOCALAPPDATA%` 默认 store 天然一致，不涉及。

## Alternatives considered

- **A. 改上游 `@deepseek-ai/dsh`，让 `dsh plugin` 的 `spawnSync("pnpm")` 注入 store-dir**：原本最根治——
  dsh 知道 `DSH_HOME`，可在 spawn pnpm 时注入 `pnpm_config_store_dir`（等效 desktop 的注入），任何调用面一致。
  落败：改上游 npm 包，需上游合入 + 发版（或本地 patch），本仓库无法独立闭环；且上游对 `store-dir`
  与桌面 `DSH_HOME` 的耦合语义需反复沟通，周期不可控。
- **B.（采纳）桌面壳在已写的 shell rc 块里导出 pnpm store 目录**：只动本仓库，可发版闭环；复用
  `CliShimRegistrar` 既有幂等 rc 机制，改动面小、可测、best-effort。局限：只写已存在的 rc、bash/zsh 系
  （`.bashrc/.zshrc/.profile/.bash_profile/.zprofile/.zlogin`），覆盖项目既有 shell rc 枚举；fish 等非常见
  shell 不在枚举内（与既有 cli-shim 注册保持一致——它本就只写这批 POSIX rc）。
- **C. 写用户级 pnpm 配置文件（`~/.npmrc` / `~/.config/pnpm/rc`）把 `store-dir` 固化**：落败——已隔离 HOME
  实测 pnpm 11 **不读**这些文件的 `store-dir`（仅认环境变量/CLI flag），写了也无效。
- **D. 用户手改 `~/.bashrc`**：落败——把产品缺陷转嫁给用户手工操作，正是本次要消除的体验；且无法随程序
  批量交付。

## Consequences

- 桌面启动（`CliShimRegistrar.TryRegister`）后，用户 shell 的 rc 块同时含 PATH 与 pnpm store/cache export；
  终端 `dsh plugin` 与桌面 store 一致，`UNEXPECTED_STORE` 消除。
- `CliShimPath`/`CliShimPlanner`/`CliShimRegistrar` 增加一条 pnpm 环境变量导出面；`CliShimSetup` 记录
  store 目录供 rc 块构建。纯函数扩展、可单测；`CliShimRegistrarTests`/`CliShimPathTests` 补断言。
- **升级旧块**：`EnsureShellRcBlock` 对"已含桌面块"直接返回，前版本块（只含 PATH）因而补不进 pnpm 行。
  故新增 `CliShimPath.EnsurePnpmDirsInRc`：整文件已含 `pnpm_config_store_dir`/`pnpm_config_cache_dir`
  （无论块内块外）即幂等返回；缺则把 pnpm 行插入受控块内。这样前版本生成的块也能升级到导出 pnpm store。
- 语义变化：`BuildShellExportBlocks` 现在不只加 PATH，也导出 pnpm 环境变量（若调用方提供 store 目录）。
  不破坏既有概念（原 PATH 行为不变），测试按"新增强化"补断言，不改旧断言语义。
- best-effort：写 rc 失败仅告警（`CliShimRegistrar` 已吞预期异常），绝不阻断启动。
- 回归风险：rc 块内容变化不影响已存在用户的既有 rc（幂等 + 只写已存在的 rc + 不动用户内容）。
- **已知限制（有意取舍，非缺陷）**：
  - *尊重用户显式配置*：`EnsurePnpmDirsInRc` 以"整文件已含 `pnpm_config_store_dir`/`pnpm_config_cache_dir`"
    作幂等守卫，**不比对值**。若用户已显式导出别的 store（如 `~/.local/share/pnpm`），守卫短路、跳过
    覆盖——保留用户自选配置，符合"不覆盖用户配置"的项目原则，代价是该用户不会被强制对齐到 `~/.dsh/.pnpm-store`。
  - *新目录名仅在注册时点固化*：rc 块以 `RcBeginMarker` 为幂等键、注册时写入一次；pnpm 行烘焙的是注册当时的
    `ResolveDshHome()` 结果。若用户之后显式改 `DSH_HOME`/`DSH_DESKTOP_DSH_HOME`，rc 里仍黏旧 store。
    与既有 PATH 行的"注册一次"机制一致，属可信边界；换 home 属用户显式操作，必要时重跑一次 `CliShimRegistrar`。

## Related

- [simple-shell-single-global-dsh](../architecture/2026-08-31-simple-shell-single-global-dsh.md)（implemented）：
  本 ADR 落实其"dsh 已全局在 PATH，仅 pnpm shim + PATH 注册"里未覆盖的 pnpm store 一致性面。
- [reference-alignment](../architecture/2026-08-29-reference-alignment.md)（implemented）：CLI shim / shell rc
  幂等块机制的出处。
- [cookbook（docs/cookbook.md）](../../../../docs/cookbook.md) [上游] dsh `plugin` 经 `spawnSync("pnpm")` 从 PATH 调 pnpm，无捆绑——本 ADR 的
  实证依据之一，恢复终端与桌面一致正是对这一上游行为的桌面对齐。
