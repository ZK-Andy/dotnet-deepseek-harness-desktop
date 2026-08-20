# Agent Note: pnpm-store-ci-cache

Status: implemented

## Problem

三平台打包（尤其 Windows，12.7 分）最重的两个步骤之一是「生成捆绑运行时」（`bundle-runtime-ci.sh`，Windows ~286s）。旧实现每次在 `mktemp -d` 的临时 store（`--store-dir "$TMP/store"`）里从零重建 ~1.5GB dsh 闭包：重下包、为当前平台重编原生模块（node-pty/koffi/protobufjs/@google/genai/dsh-subprocess-local）、重拷整棵 node_modules。各 workflow 本有 `缓存 pnpm store` 步骤（路径 `~/.local/share/pnpm/store`，键 `pnpm-store-v1`），但脚本用临时 store 完全绕开了它——**缓存形同虚设（实测仅积累 57MB）**；且旧缓存键跨平台共用同一个 `pnpm-store-v1`，若真起效反而会把 Linux 的原生二进制 restore 到 Windows/macOS。

## Decision

**把 pnpm store 接入 CI 跨 run 持久化缓存**：

- `bundle-runtime-ci.sh` 的 store 改为环境可配：`PNPM_STORE_DIR="${PNPM_STORE_DIR:-$HOME/.dsh-pnpm/store}"`，两处 `pnpm add --store-dir "$PNPM_STORE_DIR"`；`$HOME/.dsh-pnpm/store` 在 Git Bash（Windows runner）亦正确解析，三平台统一。
- `scripts/bundle-runtime.sh`（本地薄封装）默认把 store 放工作区 `$ROOT/.cache/pnpm-store`（沙箱 /home 只读时默认 `$HOME` 会失败），`.gitignore` 增 `/.cache/`。
- 三平台 `package-*.yml` 的缓存步骤：`path` 改为 `~/.dsh-pnpm/store`，键改为**按架构隔离** `pnpm-store-${{ matrix.rid }}-v2`（Windows 无矩阵用 `win-x64`），`restore-keys` 用同架构前缀 `pnpm-store-${{ matrix.rid }}-`（同架构跨版本复用，pnpm store 本身按版本累积）。

## Alternatives considered

- **保留临时 store（现状）**：落败——每次全量重建，Windows 260–290s 雷打不动，缓存步骤彻底无用。
- **缓存最终 `resources/runtime` 闭包**：落败——缓存体 ~1.5GB 太大、post-save tar 每次重传成本高；且可能 restore 到陈旧闭包，正确性风险高于 store 缓存（store 只缓存原材料，闭包每次由 `pnpm add` 现组）。
- **缓存键只按版本、跨平台共用**：落败——pnpm store 内容含平台原生二进制，跨架构 restore 会污染，故按 `matrix.rid` 隔离。

## Consequences

- **实测（v0.1.18 + 手动复跑）**：store 缓存机制本身有效（二次同分支 run 命中 `pnpm-store-win-x64-v2`、不再 re-save），但**对 Windows 捆绑提速基本无效**——缓存命中时「生成捆绑运行时」仍 ~283s，与 miss 时 277–310s 相当。真正瓶颈是 `cp -Lr` 把 ~1.5GB `node_modules` 闭包拷进 `resources/runtime`（数万文件/解引用，Windows 慢），**与 pnpm store 无关，store 缓存无法加速**。job 只从 miss 的 ~14.1 分降到 hit 的 ~12.4 分（靠省掉 ~110s 的 store re-save）。
- **GitHub Actions 缓存按分支/ref 作用域隔离**：v0.1.18 在 `refs/tags/v0.1.18` 存的缓存，`main` 上手动 run 看不到（`Cache not found`）；同分支二次 run 才命中。tag 触发的发布流每个 tag 是独立 ref，store 缓存跨 tag 亦难命中。
- **结论/转向**：要真正提速 Windows 捆绑，应缓存**组装好的 `resources/runtime` 闭包**（键 = DSH_VERSION+NODE_VERSION+arch），命中时整步跳过（免下载/免 pnpm/免 `cp -Lr`）；store 缓存改为仅作该方案的次要层。另注意 Windows「打 Windows 包」~345s 的墙体大头是 Inno Setup `LZMA` 压缩 ~1.5GB 闭包（见 `drop-standalone-zip-artifacts`），需靠闭包瘦身（per-arch 裁剪专案）或压低压缩级别才能再降。
- 验证：三平台 `Post 缓存 pnpm store` save（Linux 2–4s / macOS 7–15s / Windows ~110–125s）；同分支二次 run 命中。
