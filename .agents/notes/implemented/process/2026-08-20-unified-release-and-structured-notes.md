# Agent Note: unified-release-and-structured-notes

Status: implemented

## Problem

Release 由三个独立打包 workflow（`package-linux/macos/windows.yml`）各自的 `publish-release` 作业拼装，每个都用 `softprops/action-gh-release` 且都开 `generate_release_notes: true`——三个作业并行各自生成一次正文，叠加导致 `v0.1.16` 的 body 是同一行 "Full Changelog" **重复 3 遍**；且无任何结构化说明，release「简陋」。

## Decision

参照 deepseek-ai/deepseek-harness 的 `dsh-v0.1.0-rc.8` release（结构化双语正文），重构为**统一发布 + 脚本生成结构化正文**：

- **新增统一 `.github/workflows/release.yml`**（`push: tags v*`，`fetch-depth: 0`）：download 三平台 `*-packages` 产物 → 生成合并 `SHA256SUMS.txt` → 用 `scripts/release-notes.sh` 生成正文 → `softprops` 创建**单个** Release（`generate_release_notes: false`，含 `rc./beta/alpha` 自动标 `prerelease`）。单一 owner，杜绝重复与正文竞争。
- **新增 `scripts/release-notes.sh`**：从 `git log`（conventional commit）按类型归类生成结构化正文（新增/修复/优化/文档/构建·CI，中英小节头 + 中文 commit 列表 + compare 链接；自动整句英译不可靠，正文中文为主，与仓库「默认中文」一致）。
- **三平台 `package-*.yml` 移除各自 `publish-release` 作业**：只出包 + 上传 `7 天` artifacts；发布统一由 `release.yml` 在 tag 时聚合。
- **tag 命名保持 `v0.1.x`**（单应用惯例，不加作用域前缀）。

## Alternatives considered

- **最小改动（只留一家生成正文）**：落败——仍有并行竞争与所有者不清，正文重复根因未除净。
- **手动维护 release-notes.md**：落败——结构易漂移、易漏，改用脚本从已分类的 commit 自动生成更省、更一致。
- **tag 加作用域前缀（如 `desktop-v0.1.x`）**：落败——本项目是单一应用非 monorepo，裸 `v0.1.x` 更符合惯例；作用域前缀留待将来真有多组件再议。
- **自动整句英译正文**：落败——机器翻译不可靠、成本高；采用中英小节头 + 中文 commit 列表，兼顾双语可读与真实。

## Consequences

- 收益：release 由单一 workflow 幂等发布，正文结构化、不再重复；tag 触发一次即聚合三平台 + 合并校验和。
- 代价/注意：`release.yml` 需在真 tag 触发的 CI 上验证（本地不能跑 workflow）；正文自动归类依赖 commit 前缀规范（不规范的 commit 落入「构建·CI·其他」或缺失类型分区）。
- 校验：合成 `SHA256SUMS` 覆盖全部资产；`prerelease` 按 tag 是否含 `rc./beta/alpha` 自动判定。
