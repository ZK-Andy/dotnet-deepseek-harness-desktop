# Agent Note: pin-pnpm-1170-for-bundled-closure

Status: implemented

## Problem

`bundle-runtime-ci.sh` 组装 dsh 闭包依赖 pnpm，但版本从未钉死（`if ! command -v pnpm` 时 `npm install -g pnpm@11` 只覆盖无 pnpm 的机器；runner 预装版本随镜像更新漂移）。v0.1.20（内置 dsh 0.1.1-rc.1）tag 触发后三平台打包全失败：CI runner 预装有 pnpm 11.22.0（脚本的安装分支被跳过），`pnpm add dshmarket@1.15.0` 重新解析时按严格 node-semver 预发布规则解析 dshmarket 的 **optional peer** `@deepseek-ai/dsh-settings@^0.1.0-rc.7`——已发布的 `dsh-settings@0.1.1-rc.1`（tuple 0.1.1）因无同 tuple 且带预发布标签的比较器，不满足 `>=0.1.1 <0.2.0-0` 而 ERR_PNPM_NO_MATCHING_VERSION，构建确定性失败。本地固定 pnpm 11.7.0 构建同一 DSH_VERSION 完整成功（闭包 dsh web: 自检 OK，且 11.7 的依赖图未要求 dsh-settings）。

## Decision

把 pnpm **钉到本地已验证的 11.7.0**，且不依赖 runner 预装状态：非 `11.7.0` 一律 `npm install -g pnpm@11.7.0` 对齐，再 `pnpm --version` 复核；安装失败由 `set -euo pipefail` fail loud。CI 与本地构建环境由此一致（同一 pnpm 版本 → 同一依赖图 → 同一闭包）。

## Alternatives considered

- 改用 pnpm >= 11.22 并对 dsh-settings 显式 `pnpm add @deepseek-ai/dsh-settings@0.1.1-rc.1` 钉入：落败——会把上游打包缺陷（peer 区间声明与已发布版本不匹配）固化进我们的闭包图，且 peer 区间校验仍可能硬失败；钉版本更稳。
- 升级到 pnpm 12 及以上：落败——尚未验证；11.7.0 已是全链路（装包/原生构建/自检）验证过的环境，升级引入新变量。
- 只改 fallback 分支为 `pnpm@11.7.0`：落败——runner 预装 pnpm 时分支不执行（v0.1.20 实证），必须显式版本核对 + 对齐。
- 放弃 0.1.1-rc.1 回退 rc.8：落败——用户要求升级，且失败属构建环境而非运行时；本地闭包已是 0.1.1-rc.1 且自检 OK。

## Consequences

- 收益：CI 构建环境确定化，0.1.1-rc.1 闭包可按本地已验证的同一 pnpm 版本重建；此钉版对未来 DSH_VERSION 升级同样生效。
- 代价/风险：pnpm 11.7.0 是旧版（2026-08 时 11.22.0 已有更新），可能缺后续修复；上游若修复 dsh-settings peer 区间后可放宽钉版（届时按新证据重评）。runner 无权限全局安装时 fail loud（可加 sudo 或换安装方式兜底）。
- 验证：本地 `bash -n` 语法 OK + 11.7.0 重建闭包自检 OK；CI 待 tag 重跑/手动触发复核（三平台）。
