# Agent Note: 认证链 HTTP 复演诊断与 mac CI 止血

Status: implemented
Archived: 2026-09-28

Review: FULL/2026-09-27/R1=ok R2=ok R3=ok

Related: 前序 `bug-fix/2026-09-27-macos-cookie-grace-reload-and-witness-gate`（grace 未治愈，H2 确证）+ dsh `dsh-client-connection` `authorizeIndex` 源码（token→303+Set-Cookie→/ 凭 cookie 200）+ saucer `src/wk.webview.mm`（async loadRequest、无同步 hang）+ Ryn `RynWebView.cs:445`（eval 结果靠页面贴回）+ dispatch run `36262291024`（grace 开火、见证脚本 bug 红）。

## Problem

grace 批验证把 401 墙定界到 H2（cookie 从未落盘，非存慢）：grace 重载后像素仍是 401。但两个问题悬空：

1. **M/R 未分**：401 文本是"dsh 铸过币但 WebView 没带 cookie"（M）还是"token 根本没被认、从未铸币"（R）——dsh 侧 mint 日志在 CI 拿不到，客户端日志区分不了。猜 M 修 cookie，猜 R 查 token，猜错即返工。
2. **CI 见证门自残**：`smoke_capture_witness` 的 `echo "…$sd）"` 在 mac bash 3.2 下把全角括号字节误认为变量名（`sd�: unbound variable`，set -u 炸），门红在脚本 bug 而非像素；且 mac 矩阵缺 `fail-fast: false`（Linux 有），arm64 一红即取消 x64，后者证据恒丢失。

## Decision

- HTTP 复演诊断（`Infrastructure/Runtime/DshAuthReplayDiag.cs`，环境变量 `DSH_DESKTOP_DIAG_HTTP_REPLAY=1` 开启，默认关闭零行为变更；用完即删）：托管 `HttpClient`（禁自动重定向、禁自动 cookie）对同一 URL 走 dsh 同款三步——裸 `/`（可达性）、token 跳（记状态/Location/Set-Cookie 名）、凭捕到的 cookie 再访 `/`——结论行 `mint=yes/no；cookie-followup=200/401/skip` 直接区分 M/R。钩子在 `HarnessRuntimeHost.StartInnerAsync`（Infrastructure 侧，组合根不动）。
- 脱敏纪律：日志只记状态码/头名/字节数；token 与 cookie 值永不落盘（`TryGetToken`/`Redact` + 单测钉死：M/R 两世界断言结论行且断言秘密零出现；取消即静默收工）。
- CI 止血：见证回显 `${}` 化（lib 两处 + linux/windows 各一处潜伏，`grep -nP '\$[A-Za-z_][A-Za-z0-9_]*[^\x00-\x7F]'` 全仓审计）；mac 矩阵加 `fail-fast: false`（与 package-linux 对齐，注释写明）。
- 冒烟脚本 mac/linux 启动 env 加诊断开关（win 腿未到 settle，不碰）。

## Alternatives considered

- **给 dsh 打服务端补丁看 mint 日志**：落败——09-14 沙箱手法在 CI 不可复现（运行时 npm 装包，补丁链脆弱）；复演用公开 HTTP 语义达到同样区分度，零侵入。
- **继续猜 M 修 cookie（原生仓/同步等待）**：落败——grace 已证"等"无效；存/发机制不明时任何修法都是撞运气，用户已明确否决再绕。
- **Ryn cookie 注入 API**：落败——Ryn/saucer 层无此面（`set_persistent_cookies` 仅 WebViewPane 插件在用，Ryn.Core 主路径不用）；要加等于上游 feature，周期不可控。
- **诊断常开进产品语义**：落败——每启多一次 mint + 最长 30s 上限（10s×3，可取消）；CI 证据拿到即删，不留常开分支（R1 简化纪律）。
- **cookbook 落 bash-3.2 课**：落败——cookbook 2698/2700 无余量（本批实测 2736 越线）；课暂寄本 ADR，将来预算修剪出空间再归位（单一事实源不双记）。

## Consequences

- 下一次 mac dispatch 直接回答 M/R：复演 200 + WebView 401 ⇒ H2 在 WKWebView cookie 存发实锤，下批走原生仓取证/上游 case；复演 401 ⇒ token 链 bug，下批查 token  plumbing（有精确方向）。
- 见证门恢复诚实：401 像素红在像素（mean/sd 行），不再红在脚本；x64 证据不再被连坐取消。
- 代价：诊断开启时启动最多 +30s（可取消）；常态关闭零影响。删除欠账：复演类 + 钩子 + env 行在 M/R 定案下批移除（本 ADR 修订或新 ADR 承接）。

## Testing

- 新增 `DshAuthReplayDiagTests` 4 例（M 世界 mint=yes/followup=200、R 世界 mint=no/skip、取消静默、token 提取脱敏；条件回环应答者模拟 dsh 门；秘密零泄漏断言）。
- `dotnet build` 0 警告；`dotnet test` 816/816（812 + 4）。
- 三脚本 `--self-test` 全绿；`verify-cookbook`/`verify-doc-budgets`/`verify-governance` 全绿（cookbook 已还原）。
- dispatch `package-macos` + `package-linux` 看 replay 结论行定 M/R（步骤：先 mac 后 linux 并行）。
