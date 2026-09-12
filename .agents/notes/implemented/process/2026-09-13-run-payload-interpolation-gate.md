# Agent Note: 工作流 run: 块事件载荷插值机械化门禁

Status: implemented

Review: FULL/2026-09-13/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

`governance.yml` 曾把 issue/PR 标题与正文直接插进 `run:` 脚本，构成脚本注入面（修复与四处 `inputs` 直插收口见 [workflow-input-env-interpolation](2026-09-13-workflow-input-env-interpolation.md)）。该批收口靠人工核对 grep——`verify-*.py` 全家没有工作流解析能力，注入面回归（新 step 又把 `${{ github.event.* }}` 写进 `run:`）无门禁可拦。同批还漏了一处：`ci.yml` 评审档位硬闸 step 的 `BASE="${{ github.event.pull_request.base.sha || github.event.before }}"` 在 `run:` 内直插事件载荷。

## Decision

`scripts/verify-governance.py` 扩展第二检查面（PyYAML 解析 `.github/workflows/*.yml`）：

- 递归收集每个 job 的 `steps`，对 step 的 `run:` 文本扫描 `${{ … }}` 表达式：命中 `github.event.`（事件载荷）或 `env.`（step env 派生值回读）即 FAIL，报 step 名与表达式。
- 豁免形态 = step 级 `env:` 键值内的插值——这是载荷进入脚本的唯一合法通道，扫描只针对 `run:` 文本，`env:`/`with:` 块不扫。
- `github.event_name` / `github.ref` 等非载荷上下文不入检查面（无攻击者可控自由文本）。
- 接线复用既有三闸：pre-commit、CI `docs` job、AGENTS.md 质量门清单，无需新增调用点。

边界：本门禁只拦**仓库自身工作流**的回归；第三方 action 的 `with:` 输入注入面不在本仓可校验范围。

## Alternatives considered

- **正则切 `run: |` 块、不引 PyYAML**：落败——块状标量缩进/别名/多文档靠手写解析必然留误报面，YAML 结构本身就该交给解析器；runner 预装 PyYAML，导入失败 fail loud 并提示安装。
- **顺带禁 `inputs.*` 直插**：落败——`inputs` 仅具 dispatch/workflow_call 权限者可影响，威胁面与事件载荷不同级；且既有收口已全部落 step 级 `env:` 形态，禁令只增误报不加防线。
- **CI 单独 job 跑**：落败——与既有 `docs` job 文档门禁同质（纯静态扫），单独 job 只多 runner 成本。
- **只报 warning 不 fail**：落败——门禁必须 fail loud（徽章一致性门禁同判据），warning 没人看。

## Consequences

- 收益：`run:` 内事件载荷插值在 pre-commit 与 CI 两处被拦；`github.event.*` 回归即红。
- 代价：检查面依赖 PyYAML；workflow YAML 语义变化（如 step 换非标键名）须同步 `_walk_steps`。
- `ci.yml` 评审档位硬闸的 `BASE` 本批收口为 step 级 `env:`（`REVIEW_BASE`）——门禁首跑即抓到的漏网，证明检查面有效。

## Testing

`python3 scripts/verify-governance.py --self-test`：4 断言（step 级 `env:` 豁免不误报、`run:` 直插载荷/env 回读各报一、`with:` 块不误报、`event_name`/`ref` 非载荷不误报）。活扫描：全工作流绿（`ci.yml` 收口后）。

## Related

- [workflow 事件输入插值经 env: 收口](2026-09-13-workflow-input-env-interpolation.md)：注入面判定与手工收口先例；本篇把该收口的维持义务机械化。
- [评审档位硬闸](2026-09-03-review-tier-escape-proofing.md)：被收口的 `BASE` step 的行为事实源。
