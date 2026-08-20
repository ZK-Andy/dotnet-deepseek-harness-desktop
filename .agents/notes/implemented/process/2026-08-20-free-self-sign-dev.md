# Agent Note: free-self-sign-for-dev-builds

Status: implemented

## Problem

代码签名（macOS Developer ID + 公证、Windows Authenticode）需要付费证书，而本项目**没有配置任何签名凭据**（`codesign`/`signtool` 证书均未配），发布包长期处于未签名状态。开发/内部测试阶段想要"消除本机告警"、验证签名链路，但没有免费且受公开信任的路径；此前 `package-macos.sh`/`package-windows.sh` 仅有 `签名占位` 注释，无任何签名代码。

## Decision

在打包脚本里落地**免费自签/adhoc 签名管道**，专用于**内部测试与开发机**，明确标注其**对终端用户不产生信任**：

- **macOS**（`scripts/package-macos.sh`）：新增 `sign_macos()`——`SELF_SIGN=1` 时用 `codesign --force --deep --sign`（默认 identity `-` 为 ad-hoc；可用 `MACOS_SIGN_IDENTITY` 指定已有身份），随后 `codesign --verify --deep --strict` 校验；缺 `codesign` 或校验失败即 `fail loud`（`exit 1`）。
- **Windows**（`scripts/package-windows.sh`）：新增 `find_signtool()`/`ensure_self_sign_cert()`/`sign_windows()`——`SELF_SIGN=1` 时用 `signtool`（Windows SDK）以 `CurrentUser\My` 里 `CN=DeepSeek Harness Desktop Dev` 的自签代码签名证书签 `DeepSeek.Harness.Desktop.exe` 与安装器 `…-setup.exe`（证书缺失则 `New-SelfSignedCertificate` 自动创建）；缺 `signtool` 或签名失败即 `fail loud`。
- **开关语义**：默认**关闭**（`SELF_SIGN` 未设为 `1` 即不签名），现有 CI 发布（tag 触发的公开包）行为不变、保持未签名；显式 `SELF_SIGN=1` 才走自签，无论证失败即失败，杜绝"明确要求却静默跳过"。
- **CI 接缝**：`package-macos.yml`/`package-windows.yml` 各加 `self_sign` 的 `workflow_dispatch` 输入（boolean，默认 `false`），并接到作业 `SELF_SIGN` 环境变量——内部测试可经 `workflow_dispatch` 拿自签产物，tag 发布路径不受影响。

## Alternatives considered

- **真证书签名（Apple Developer ID / Windows OV-EV / Azure Trusted Signing）**：落败（否决）——需付费证书；**本项目为开源项目，不做付费签名**（无预算、将来大概率也没有），故不纳入待办；本决策铺好免费自签管道，并把 `MACOS_SIGN_IDENTITY`/`SELF_SIGN` 设计成"若有朝一日拿到真身份可无缝替换"的接缝。
- **默认开启自签**：落败——会让现有 CI 在无相关工具/环境的 runner 上新增失败点，干扰已全绿的发布链路；自签只服务内部/开发，默认关 + 显式开更契合"opt-in"。
- **不写签名、只留文档**：落败——用户希望现在就拿到能消除本机告警的内部自签产物，而非仅一份说明；脚本内自签是可立即执行的。
- **微软商店/MSIX（$19）分发**：落败（否决）——能真免 SmartScreen 但改变分发模型、需一次性付费与商店账号；与"开源不付费、自签仅开发/内部"的取向不符，不采纳。

## Consequences

- 收益：开发/内部测试可经 `SELF_SIGN=1`（或 CI `workflow_dispatch: self_sign=true`）得到已签名（ad-hoc/自签）产物，消除本机"来源不明/未知发布者"告警；脚本对"明确要求签名却无法执行"会 `fail loud`。
- 边界（很明确，已写入 README）：自签/ad-hoc **不消除终端用户**的 Gatekeeper/SmartScreen 告警——信任链由苹果/受信 CA 签发，免费路径不存在；开源项目**不做付费签名**，终端用户遇告警属预期（右键→打开 / 更多信息→仍要运行）。
- 代价：自签证书需在每台机/每次 CI 时创建或复用，非跨机持久信任；Windows 自签为 `CurrentUser` 级，随账号/会话浮动。
- 遗留：无——付费签名（真证书/notarization/Azure Trusted Signing/MSIX）经评估为开源项目不采用，已从待办移除；保持"未签名 + 免费自签"为最终取向。
