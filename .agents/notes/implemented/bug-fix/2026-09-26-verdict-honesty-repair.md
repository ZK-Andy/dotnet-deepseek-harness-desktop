# Agent Note: verdict-honesty-repair（verdict 诚实化修补）

Status: implemented

Review: FULL/2026-09-26/R1=ok R2=ok R3=ok

Related: 直接起因是 dispatch run `36237256739` 的人眼复核——amd64 verdict 绿而截图为 401 一行字（`dsh web authentication required...`）；对照 run `36233266978` 同 verdict 绿而截图为大厅 UI。终页内容非确定（时而 UI 时而 401），落定门只看到达故盲区。

## Problem

三处事实性欠账，一次性还清：①落定门（前批"①后到达≥2"）对 401 页同样放行——verdict 绿不再蕴含终页正确，此前"首绿/连绿"结论把门绿当成了产品好，错；②`NavigateAsync` 超时修法基于错误模型（以为 async 挂起，实为 Ryn 底层同步 `set_url` 原生 hang，RynWebView.cs:413 实证）——WaitAsync 计时器挂不上，7655465 未修到病根，Ryn#101 亦按此错误模型所报，本批关闭认错；③绿跑删日志——自愈触发与否、探针所见内容，在绿跑中无从查。

## Decision

- 同步原生调用隔离（产品）：`Task.Run` 包 `NavigateAsync(...).AsTask()` 再 `WaitAsync(NavCallTimeoutSeconds)`——线程池线程卡死在原生里可被计时器解绑（泄漏一线程 + loud，沿既有先例可接受）；应用退出 OCE 照常上抛。上一批的 WaitAsync 保留（异步段仍需它），补上缺失的隔离段。
- GiveUp 进门（smoke-settle-lib 落定成功分支）：日志出现 `鉴权页自愈失败` 即落定也判 FAIL（确认坏页机器可判，不再只靠人眼）；文案与既有二态一致。
- 内容留痕（产品）：`SettleWebSessionAsync` 全路径 loud——首探 Healthy 记可见文本字数免自愈、耗尽 Unknown 记放行（既有成功/放弃行不动）。长度不记内容，防敏感落盘。
- 成功留尾（smoke_deb）：PASS 也打印应用日志尾部 30 行再删——绿跑的导航/探针/自愈行从此可查，打破"绿即无证"（仓内尾部惯例 30 行）。rpm 容器腿不同治：其成功即安装链（无窗无导航），引导启动行已 live 回显，证据齐，无需留尾。
- 纪律（本批起）：verdict 结论必须附当轮截图目检——门只证到达，内容归人眼 + 内容行，两者齐了才算数。

## Alternatives considered

- **回滚落定门到 token 行**：落败——退回 Linux 恒红，等于用门红掩盖产品时好时坏；诚实红（GiveUp 门 + 人眼）比恒红有用。
- **截图 OCR 断言内容**：落败（第三次重申，前两批已否）——runner 无 OCR。
- **GiveUp 不进门、只靠人眼**：落败——确认坏页机器可判，留给人眼是懒惰；人眼负责"绿是否真好"，机器负责"坏必须红"。
- **Task.Run 改用专用线程/取消原生调用**：落败——原生 hang 不可取消（非托管阻塞），专用线程与池泄漏等价；池 + loud 最简。

## Consequences

- 绿 verdict 仍不等于终页正确（到达门语义），但 401 确认页必红（GiveUp 门）+ 每轮绿有尾日志 + 有截图——"绿+UI"结论可逐轮实证，不再靠推断。
- 代价：绿跑 CI 日志 +30 行；悬空池线程（hang 场景）；cookie 时好时坏的真病灶仍在（下批：P0 自愈触发证据齐后，主攻 token→cookie 链稳定性）。
- Ryn#101 关闭认错（见 Testing）。

## Testing

- 自测：GiveUp 落定转失败用例；Task.Run/内容行/留尾沿既有先例（编排薄层不单测，配置矩阵已覆盖超时键）。
- dispatch：逐轮取 verdict + 尾日志 + 截图三件，绿必须配 UI 图才算数；Ryn#101 评论认错后关闭。
