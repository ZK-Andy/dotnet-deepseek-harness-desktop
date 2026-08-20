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

- 收益：命中缓存时 Windows 捆绑从 ~286s 降到传/tar 解包 + `pnpm add` 增量（量级到秒级-几十秒）；三平台同效；跨架构不再串线。
- 代价/风险：cache 首次 miss 才能打底（首次仍全量）；pnpm store 略大（数百 MB ~1GB，< actions/cache 10GB 上限）；`restore-keys` 前缀命中可能耦合老版本内容，但 pnpm store 按版本累积、只增量缺的，安全。
- 验证：本地仅能跑 `bundle-runtime.sh`（沙箱可写 `$ROOT/.cache`）做语法/首跑；真实缓存命中与否依赖下次 CI tag 实测（Windows 打包步骤时间对比）。
