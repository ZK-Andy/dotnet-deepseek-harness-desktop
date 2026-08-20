# Agent Note: remove-linux-main-push-packaging-trigger

Status: implemented

## Problem

`package-linux.yml` 是三个打包 workflow 里唯一在 `push` 上触发 `branches: [main]` 的（mac/win 仅 `tags v*` + 手动）。因此**每次 commit 推到 main 都自动跑一次 Linux deb/rpm 全量打包**——每次要重下 ~421M dsh 闭包 + `dotnet publish` + 打 deb/rpm，非常浪费 runner 时间；普通提交本应只由 `ci.yml` 做 build/test/coverage。

## Decision

**移除 `package-linux.yml` 的 `branches: [main]` 自动触发**，`on` 改为与 mac/win 一致：`push: tags ['v*']` + `workflow_dispatch` 手动。同步更新头部注释（预览 = 手动无 tag）。

- 提交推 main → 不再自动打包 Linux（`ci.yml` 正常跑测试/覆盖率）。
- 打 `v*` tag → 仍自动出 Release 包（`publish-release` 与 `tags v*` 保留，正式发布不受影响）。
- 要预览包 → 手动 `workflow_dispatch` 触发。

## Alternatives considered

- **全部改手动（连 `tags v*` 也去掉）**：落败——正式发布需要 tag→Release 自动化出包；全手动会在发版时引入额外手工步骤与出错面。
- **保持现状**：落败——每次 main 提交都白耗一次全量打包 runner 时间，与 mac/win 不一致。
- **改成 `paths` 过滤（仅打包脚本变更才触发）**：落败（另议）——细粒度触发需维护路径清单，且仍会在相关提交时自动跑；本次目标是"提交即打包"这个浪费，直接对齐 mac/win 手动模型更简单。

## Consequences

- 收益：main 提交不再触发 Linux 打包，runner 时间回落，三个打包 workflow 触发语义一致。
- 代价：main 上不再自动产出 Linux 预览包；需要预览时改走手动 `workflow_dispatch`（多一步），可接受。
- 一致性：`package-linux/macos/windows.yml` 三者现均为 `tags v*` + 手动。
