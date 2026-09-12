# Agent Note: workflow run: 内事件输入插值统一经 env:

Status: implemented

Review: FULL/2026-09-13/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

GitHub 对 `${{ }}` 插值是先拼接进脚本再解析——事件输入值含 `$(...)`、反引号或换行即可脱离字符串字面量成为脚本片段（governance.yml 的 PR 正文注入即此机理，`ed10f81` 已收口）。本批前的字符面清点发现同构直插点还有四处：`package-linux.yml` 两处、`package-macos.yml` 一处、`package-windows.yml` 一处的 `${{ inputs.version }}` 直插 `run:`；另有三个 `package-*.yml` 的 publish 步把经 `GITHUB_ENV` 回流的 `${{ env.VERSION }}` 再插值进 `run:`——同源（`inputs.version` 可影响其值）的间接通道。`inputs.version` 仅具 dispatch 权限者可影响，风险低，但注入面按权限分级保留不是收敛。

## Decision

事件输入与其派生值的脚本通道全部收为环境变量：

- 四处 `${{ inputs.version }}` 改经 step 级 `env:`（`INPUT_VERSION: ${{ inputs.version }}`）传递，脚本内以 `"$INPUT_VERSION"` 引用——值作为单个环境变量进入脚本，不参与 bash 语法解析。
- 三处 publish 步的 `-p:Version=${{ env.VERSION }}` 改为 `-p:Version="$VERSION"`（`GITHUB_ENV` 写入的变量在本就作为环境变量存在于后续步骤，无需再插值）。
- `package-windows.yml` 的 publish 步显式加 `shell: bash`：Windows runner 的 `run:` 缺省 shell 是 pwsh，`"$VERSION"` 会被当 pwsh 变量展开成空串（macOS/Linux 缺省即 bash）。
- 同文件其余 `${{ }}`（`github.event_name`/`github.ref`/`matrix.*`）为 runner 上下文而非事件载荷，不可被外部输入影响，保持原样。
- 机械化（`verify-governance.py` 增「`run:` 内禁插值事件载荷及其派生 `env.*`、豁免 step 级 `env:` 形态」检查）记为跟进：需解析 YAML 块标量与豁免形态，与本批分开评审。

## Alternatives considered

- **只修 package-linux、macos/windows 记跟进**：落败——R2 评审实测三文件同构，半批修复让「清点结论」与仓库现实分叉；同形改动边际成本两行，一次收口回滚粒度不变。
- **维持现状（inputs 仅 dispatch 权限者可写）**：落败——同构面已在 governance.yml 实测被 PR 正文打红，字符面清点后仍留直插等于承认「注入面按权限分级保留」，而修复只有数行。
- **一并机械化 `verify-governance.py`**：落败（暂缓为跟进项）——检查器要解析 YAML 的块标量结构并豁免 `env:`/`if:` 形态，误报会把全部 workflows 判红；与本批卫生修绑同批会让回滚粒度失真，记为跟进项。
- **改用 `github.event.inputs.version` 等 context 替换**：落败——换 context 不改变「插值进脚本」的本质，只有经 `env:` 传递才把值隔离在 bash 语法之外。

## Consequences

- 收益：事件输入直插 `run:` 的形态在本仓 workflows 清零（含 `GITHUB_ENV` 回流的间接通道）；版本号到脚本的唯一通道是环境变量。
- 代价：`package-windows.yml` publish 步从 pwsh 换为 bash——语义上更贴近同构的 macos/linux 步；bash 在 windows runner 由 Git Bash 提供，该步此前已有多处 bash 步先例。
- 跟进：`verify-governance.py` 机械化检查另行立项，落地前本判据靠评审清单与字符面清点维持。

## Testing

- 本地：`python3 scripts/verify-governance.py` 绿（现不扫 `inputs.*`/`env.*`，表达合法性由 dispatch 实跑验证——见下）。
- 语义等价：`git show HEAD` 对照——空输入时 `INPUT_VERSION` 为已定义空串（`set -u` 安全），「输入留空回退 csproj」与 tag 覆写分支逐字不变；publish 步 `-p:Version=` 的值同样来自 `VERSION` 变量。
- dispatch 实跑（推送后）：`package-linux.yml` 以 `workflow_dispatch` + `inputs.version` 非空触发一次，验证「确定打包版本」步经 `INPUT_VERSION` 产出与改前一致的版本判定。

## Related

- [review-tier-escape-proofing](2026-09-03-review-tier-escape-proofing.md)：workflows 为行为契约面（FULL 触发）的判定出处。
