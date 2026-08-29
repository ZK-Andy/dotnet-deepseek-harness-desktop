# Agent Note: 修复市场后台安装链路（假包 + allowBuilds + 检测）

Status: implemented

## Problem

`0.1.8–0.1.10` 的市场预装始终未生效：`0.1.8` 同步 `desktop.patch.yml` 阻塞 `dsh web:` 致重启循环，`0.1.9` 回退为手动但 `bundles` 仍无 `dshmarket`，`0.1.10` 后台 `file://` 安装亦失败（`journalctl` 显示 `exit=1 ERR_PNPM_IGNORED_BUILDS`，`profiles/web/package.json` 残留 `dependencies.app=file:.../dshmarket.tgz` 且 `bundles` 仅含 `[@deepseek-ai/dsh-base, @deepseek-ai/dsh-web-app]`）。用户实机 `0.1.10` 复现：`dshmarket.tgz 394B` 为 `app` 壳包、`pnpm-workspace.yaml` 的 `allowBuilds` 仍为占位 `set this to true or false`（缺 `esbuild`）、`Program.cs` 的字符串检测与“补 bundles”未落地（仅日志）。

## Decision

分三段修复市场链路，`v0.1.11` 闭环安装、`v0.1.12` 补重启以即时生效：

- **闭包 tgz**：`scripts/bundle-runtime-ci.sh` 不再 `pnpm pack dshmarket`（该命令在 `$TMP/app` 下打包 `app`），改为直接 `curl https://registry.npmjs.org/dshmarket/-/dshmarket-1.15.0.tgz -o resources/runtime/dshmarket.tgz`（`497K` 官方已构建包，跳过 `tsc/prepack`），失败回退为本地 `lib/client` 的 `tar --transform package/`。增加 `>10K` 与 `package/package.json name==dshmarket` 双校验；`--store-dir "$TMP/store"` 规避 `~/.local/share/pnpm/store` 的 `sqlite` 锁。`resources/runtime` 保持 `pilot-harness` 整树 `node_modules` 模型。

- **后台安装**：`src/DeepSeek.Harness.Desktop/Program.cs` 重写为 JSON 精确检测（`dependencies.dshmarket && bundles.contains(dshmarket)` 才跳过）、迁移清理 `0.1.10` 误写入的 `dependencies.app=file:...dshmarket.tgz`、确保 `pnpm-workspace.yaml` 的 `allowBuilds`（`@deepseek-ai/dsh-subprocess-local/@google/genai/koffi/node-pty/protobufjs/esbuild` 均为 `true`）、`ResolveMarketSpec` 优先校验过的 `tgz`（`>10K`）否则回退 `resources/runtime/node_modules/dshmarket` 目录或 `dshmarket@1.15.0`，`EnsureBundlesContainsMarketAsync` 兜底写回。进程仍为 `bundled node + dsh/lib/bin.js plugin --profile desktop add <spec>`（共享 home 切换前的历史版本为 `--profile web`），`DSH_HOME/.pnpm-store` 注入不变，不阻塞首启（`3s` 延时后台）。`v0.1.12` 起安装成功后不再仅 `NavigateAsync(webUrl)`，而是 `EvaluateJavaScriptAsync(RecoveryScript)` + `host.Stop()` 交由 `RuntimeSupervisor` 重启 `dsh` 并导航新 `URL`（仅刷新不重启无法让服务端重载 `package.json`）。

- **打包校验**：`scripts/package-linux.sh` 在 `staging` 即 `fail loud` 校验 `dshmarket.tgz`（`>10K` 且 `name==dshmarket`）与 `node_modules/dshmarket` 存在，杜绝 `394B` 假包流入 `deb/rpm`。

## Alternatives considered

- **继续 `pnpm pack` 并加 `--ignore-scripts`**：让 `pnpm pack` 打出 `dshmarket` 需绕 `tsc/prebuild` 与 `npm_config_cache EROFS`，且 `pnpm pack dshmarket` 语义仍为打包 `app`。落败原因：官方 `tgz` 已构建，`curl` 零构建且与 `pnpm add dshmarket` 在闭包中已装的 `node_modules/dshmarket` 同源，链路更短。

- **完全走 `registry` 直装（`spec=dshmarket@1.15.0`）不随包 `tgz`**：省去 `tgz` 生成与校验。落败原因：离线/弱网首启失败，且已验证 `file:resources/runtime/node_modules/dshmarket` 目录安装亦可，保留 `tgz` 为离线最短路径，回退才走目录/registry。

- **同步安装（阻塞 `dsh web:` 前 `plugin add`）**：确保首启即有市场。落败原因：`0.1.8` 已证明同步 `pnpm add`（含 `supply-chain` 校验 `~10s`）会拖过 `60s` 窗口致 `starting→shutting down`，与 `Tauri` 推荐的后台预装一致，窗口先亮更优。

- **仅修 `allowBuilds` 不修 `tgz`**：把 `pnpm-workspace.yaml` 6 项置 `true` 即可让 `0.1.10` 的 `394B` 假包 `exit 0`。落败原因：`app` 仍无 `dsh.bundle`，`reconcilePlugins` 不会写入 `bundles`，Web UI 仍无市场；且 `394B` 的 `package.json` 为 `app`，`staging` 无法自证。

## Consequences

- 收益：`0.1.11` 起 `dshmarket.tgz` 为真包（`497K`，`package/package.json name==dshmarket`），`staging --stage-only` 在假包/缺包时 `fail loud`；`allowBuilds` 自愈与 `app` 迁移使 `0.1.10` 存量 `profile` 在下次启动后台自动修复，无需用户手动 `dsh plugin add`；`bundles` 双保险确保市场可见。`0.1.12` 起安装后 `host.Stop()` 由 `RuntimeSupervisor` 重启并导航新 `URL`，首启后台安装即可即时出现市场，无需二次手动重启（`0.1.11` 仅 `Navigate` 需二次重启）。
- 代价：`bundle-runtime-ci.sh` 依赖外网 `curl` 拉官方 `tgz`（离线回退为本地 `tar`，但官方包仍为首选）；`Program.cs` 新增 `System.Text.Json` 解析与工作区改写及重启逻辑，体积与复杂度微增。
- 验证：`dotnet build/test 12/12`；`verify-adr-format/doc-budgets/md-links` 全绿；`bash scripts/bundle-runtime-ci.sh linux-x64` 产 `421M` 与 `dsh web: http://...` 自检 `OK`；`bash scripts/package-linux.sh --stage-only` 在正确 `tgz` 下通过、假包下 `fail loud`；`journalctl` 后台 `dsh plugin add exit=0` 且 `profiles/web/package.json bundles` 含 `dshmarket`，`v0.1.12` 额外验证 `host.Stop()→supervisor 重启→新 URL` 后市场可见。

## Related

- [online-first 去捆绑运行时](../../implemented/architecture/2026-08-29-online-first-unbundled-runtime.md)（implemented）：**tgz 供给面随其批次二退役**（`bundle-runtime-ci.sh` curl 官方包进闭包已删）；本篇的后台安装机制（allowBuilds 自愈 / app 迁移 / bundles 双保险 / 装后重启）仍为现行行为。
