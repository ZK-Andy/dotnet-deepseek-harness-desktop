# Agent Note: Linux packaging aligned with pilot-harness

Status: implemented

## Problem

Linux 打包在 `0.1.0–0.1.2` 迭代中逐项暴露：闭包仅拷 `dsh/.` 缺依赖导致启动失败（重连循环）、`cp -r` 改名破坏 `RuntimeLocator`、`pnpm` 忽略原生构建致缺 `.node`、`brp-strip` 误伤跨平台 prebuild、`AutoReqProv` 扫描出 `aarch64/musl/perl` 假依赖、`WebKitGTK` 版本误判（4.1 vs 6）。虽已在 `16bab10..eca6b60` 逐项修掉，但脚本是“打补丁”式累积，本地 `resources/runtime` 仍为旧 `dsh/` 布局、`bundle-runtime.sh` 与 `bundle-runtime-ci.sh` 不一致、workflow 保留 `90` 天与手动 `__requires_exclude`，与参照项目 `op7418/pilot-harness` 的打包模型未对齐。

## Decision

参照 `pilot-harness` 的 Electron 打包模型重整 Linux 链路（本项目为 `Ryn/.NET`，等价映射）：

- **闭包模型**：`asar: false` 等价——`@deepseek-ai/dsh` 依赖树原样收入，不打单文件。`scripts/bundle-runtime-ci.sh` 以 `pnpm add @deepseek-ai/dsh@0.1.0-rc.7 --prod --allow-build=*` 收集完整 `node_modules`，`cp -a node_modules/. resources/runtime/node_modules/` 保留 `pnpm` 相对 symlink；入口固定 `resources/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js`，与 `pilot-harness apps/desktop/src/main.ts:59` 的 `join(app.getAppPath(), 'node_modules', '@deepseek-ai', 'dsh', 'lib', 'bin.js')` 一致。`resources/runtime/node` 为平台 Node 二进制（`Ryn` 非 Electron，需自带 `node`；`pilot-harness` 用 `process.execPath` 复用 Electron 内置 Node）。

- **自检**：`bundle-runtime-ci.sh` 的 `60s` 超时长驻抓 `dsh web:` 与 `pilot-harness` 的 `extractHarnessServerUrl` + `60_000` 超时一致，只看日志是否出 URL，不以 `timeout` 退出码判失败。

- **安装包**：`scripts/package-linux.sh` 手工组装 `deb`（`dpkg-deb`）/`rpm`（`rpmbuild`），等价于 `pilot-harness electron-builder.yml` 的 `linux.target: [AppImage, deb, rpm]`。`linux.desktop` 对齐 `Name/Comment/Categories/StartupWMClass`；`AutoReqProv: no` + 显式 `Requires: libwebkitgtk-6.0.so.4`（`saucer` 链接 `libwebkitgtk-6.0.so.4`，非 `4.1`）以屏蔽 `node_modules` 跨平台 prebuild 产生的假依赖；`%global _enable_debug_packages 0` + `%define __os_install_post %{nil}` 禁 `brp-strip/debuginfo`（与 `pilot-harness` 用原生 `codesign` 规避 JS signer 耗尽 FD 同理）。`staging` 阶段 `fail loud` 校验 `RuntimeLocator` 入口。

- **CI**：`.github/workflows/package-linux.yml` 对齐 `pilot-harness .github/workflows/desktop.yml`：`concurrency` 组、`pnpm/action-setup + setup-node`、`7` 天 Artifacts、`if-no-files-found: error`、预览（`push main` / `workflow_dispatch` 无 `tag`）与发布（`v*` tag → `SHA256SUMS.txt` + `softprops/action-gh-release`）分离。本地不产包，所有 `deb/rpm` 来自 `ubuntu-latest` runner。

- **本地脚本**：`scripts/bundle-runtime.sh` 改为本地产出与 CI 一致的整树布局（`--from-ci` 委托 `bundle-runtime-ci.sh`；本机 `DSH_SRC` 时亦 `cp -a` 整棵 `node_modules`），不再产 `dsh/` 旧布局。

## Alternatives considered

- **继续补丁式维护现有脚本**：在 `eca6b60` 上小修并触发一次 `workflow_dispatch`。落败原因：四次失败 `16bab10/923b918/ce04473/bfcecdc` 已证明链路需“闭包—校验—依赖—strip”整体自洽，旧 `bundle-runtime.sh` 仍产 `dsh/` 与新 `RuntimeLocator` 分裂，保留 `90` 天与 `__requires_exclude` 只是延后下一次误判。

- **引入 `electron-builder` 直接打 Linux 包**：复用 `pilot-harness` 的 `electron-builder.yml` 产 `deb/rpm`。落败原因：本项目宿主为 `Ryn/.NET`（`dotnet publish --self-contained`），非 `Electron`；`electron-builder` 无法组装 `.NET` 单文件 + `resources/runtime` 的 `usr/lib` 布局，且会引入 `Node`/`Electron` 工具链冗余。

- **改用 `fpm` 一键打 `deb/rpm`**：一行命令产双格式。落败原因：隐藏 `spec/control` 细节，无法显式表达 `AutoReqProv: no`、`Requires: libwebkitgtk-6.0.so.4`、`_enable_debug_packages` 等对 `node_modules` 特例的决策；后续体积裁剪与签名需回到底层模板。

- **本地可产包（补 `dpkg-deb/rpmbuild` 到沙箱）**：让开发者本机即可验证。落败原因：沙箱 `Fedora 44` 无 `dpkg`、`/home` 只读致 `dnf`/`podman` 不可用，且用户已明确“本地无法打包，走 GitHub”；强行补工具只增本地分支，与 `pilot-harness`“官方安装包仅由 GitHub 原生 runner 产出”一致性冲突。

## Consequences

- 收益：打包脚本与 `pilot-harness` 模型一致，决策单点可追；`bundle-runtime-ci.sh` 与 `RuntimeLocator` 同口径，`package-linux.sh` 在 `staging` 即 `fail loud`；`rpm` 假依赖与 `strip` 误伤被系统性关闭；`CI` 预览 `7` 天与 `Release` 发布职责分离，与 `pilot-harness` 的 `apps/desktop/release` 产物策略同构。
- 代价：旧 `resources/runtime/dsh` 布局废弃，本地已有 `442M` 缓存需重建（`bash scripts/bundle-runtime-ci.sh linux-x64` 或 `--from-ci`）；`Artifacts` 保留期由 `90` 天缩为 `7` 天（与 `pilot-harness` 一致），`0.1.2` 的 `212MB` 唯一副本需在需要时另行归档。
- 验证：`python3 scripts/verify-adr-format.py && verify-doc-budgets && verify-md-links`；`dotnet build/test 12/12`；`bash scripts/package-linux.sh --stage-only` 在正确布局下通过、旧布局下 `fail loud`；`GitHub package-linux` 需一次 `workflow_dispatch` 全绿且 `rpm -qp --requires` 仅含 `libwebkitgtk-6.0.so.4`。
