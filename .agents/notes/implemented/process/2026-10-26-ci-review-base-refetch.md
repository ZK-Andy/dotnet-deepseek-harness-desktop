# Agent Note: CI 评审 base 游离时按 sha 补拉

Status: implemented

Review: FULL/2026-10-26/R1=ok R2=ok R3=ok

## Problem

`ci.yml` 的两道闸（增量纪律、评审档位）都以推送事件的 `github.event.before` 作 diff base。force-with-lease 改写历史后，这个 sha 不再被任何 ref 覆盖：`actions/checkout` 即便 `fetch-depth: 0` 也只取 refs 可达面，runner 本地就没有该对象，闸拿到的是 `git diff <base>..HEAD` 的 `fatal: Invalid revision range`。

run `40170939767`（2026-10-25，v0.7.2 发布面历史修复）实证：docs 作业的增量闸输出 `increment: undeterminable diff (… fatal: Invalid revision range 5de1008…HEAD)`，随后作业中止，同 run 的档位闸未执行（两闸取 base 的判据同源）。这个红不携带信息——它与「真的超四帽」「真的缺 Review 证据」落在同一个出口，操作者必须读日志才知道是「不可判」还是「判定为违规」；当时的补救是 push 一个空提交重建可判 base（同一补救在 2026-09-15 也用过一次）。

## Decision

docs 作业在两道闸之前新增一步「解析评审 base」：`BASE = pull_request.base.sha || event.before`，判出三态；`ok`/`none` 经 `$GITHUB_ENV` 交给两闸，`unreachable` 在该步直接硬红并中止作业（后续两闸不执行）。

- `none`：BASE 为空或全零（首推/新建分支）——增量闸 warning 跳过、档位闸硬红，行为与改动前一致（这仍是两闸唯一相反的一态，故两闸保留各自分支）。
- `ok`：本地已有 `git cat-file -e "${BASE}^{commit}"`；否则 `timeout 90 git fetch --no-tags --depth=1 origin "$BASE"` 后复验——GitHub 允许按 sha 取游离对象（2026-10-26 探针：空仓匿名 `--depth=1` 取回 `5de1008` 成功；端到端在缺该对象的浅仓里跑本步，state=ok 且 `git diff --numstat <base>..HEAD` 返回 33 行）。
- `unreachable`：补拉后仍缺对象——解析步一处硬红，消息点名「base 不在历史且补拉失败」并给唯一补救（push 空提交重建可判 base）。此前的形态是两闸各报一遍，其中档位闸那一臂实际永不执行（前一步已 exit 1），故统一收在解析步。

两个约束留在实现里：事件载荷只在步骤 `env:` 里落地（`github.event.*` 不进 `run:` 块，符合 verify-governance 的插值禁令），base 经 `$GITHUB_ENV` 以环境变量形式传给下游；`--depth=1` 只裁祖先关系，该提交自身的树与 blob 完整——两闸要的正是这份快照（`git diff --numstat` 与档位闸的 `git show <base>:<rel>` 都需 base 侧 blob 内容——`--name-only` 只比树——见 [increment-gate-enforce](2026-10-14-increment-gate-enforce.md) 与 [review-tier-escape-proofing](2026-09-03-review-tier-escape-proofing.md)），祖先关系无人使用，故浅化安全。

## Alternatives considered

- **维持现状（红就人工补空提交）**：落败——补救手段存在但红本身不可读，每次都要读日志分辨「不可判」与「判定违规」；空提交还会在历史里留下无意义提交。
- **base 缺失时退回 `HEAD^`**：落败——多提交推送只算最后一提交的 diff，等于让被改写的整批变更绕过四帽与档位，是静默放行而非降级。
- **加深 clone 让 git 自己取**：落败——游离对象不在任何 ref 上，`fetch-depth` 加深也取不到；只有按 sha 显式 fetch 才行（已实证）。
- **补拉失败降级为 warning**：落败——「无法判定 = 违规」是既有判据（档位侧 [review-tier-escape-proofing](2026-09-03-review-tier-escape-proofing.md)、增量侧 [increment-gate-enforce](2026-10-14-increment-gate-enforce.md)、共同前提 [ci-gate-honesty](2026-09-28-ci-gate-honesty.md)）；降级即放行未审批次，本次只把出口说清、不放松判定。

## Consequences

- 改写历史后的推送从「红得没有原因」变成「红得有名有姓」：可判时按四帽/Review 证据正常判，不可判时点名补救并保留硬红。
- 补拉不保证转绿。本地以 `5de1008` 为 base 复现的判据是 `gross 979 > 600`（v0.7.3–v0.7.5 全部历史进入 diff）——补拉把 undeterminable 换成实质判定，红可能仍在，这是预期行为。
- 依赖 GitHub「按 sha 取游离对象」这一行为；上游若收紧，`unreachable` 硬红会把原因写明，不会静默放行。该行为只对仍留在对象库中的提交成立，GC 之后补拉失败。
- docs 作业的 checkout 是 `persist-credentials: false`，补拉依赖公开仓匿名可达；仓库转私有须在此步前置凭据。
- 仅 base 游离时多一次网络往返（`timeout 90` 兜底），正常推送零成本。

## Testing

- 门的机械面：actionlint（1.7.12 钉版，`-shellcheck -S warning`）exit 0、`verify-governance.py` OK、`verify-shell-standards.sh` OK。
- 步骤行为面：以 pyyaml 取出该步 `run:` 块，在四种 base 形态下实跑——本地已有 → `ok`；空/全零 → `none`；伪造 sha → `unreachable`（GitHub 回 `not our ref`，该步 exit 1、后续两闸不执行）；缺对象但可补拉（浅仓端到端）→ `ok` 且 `git diff --numstat` 可算。
- 真 runner 面：run `40352264564`（`34d0d8d`）docs 作业实跑——解析步输出 `review base: state=ok base=01bdc22…`，两闸随后用 `DSH_REVIEW_BASE` 判定，整 job 绿；补拉分支（`unreachable`/可补拉）尚无真 runner 触发条件（需改写历史推送），其行为由上述步内实跑覆盖。
