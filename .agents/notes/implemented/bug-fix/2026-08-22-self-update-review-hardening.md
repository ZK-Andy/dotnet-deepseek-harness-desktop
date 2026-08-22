# Agent Note: self-update-review-hardening

Status: implemented

## Problem

桌面壳自更新（`2026-08-22-desktop-shell-self-update`）代码审核（2026-08-22）发现 11 项问题：dev 运行时点击更新会**装系统包后重启旧 dev 二进制**形成循环；SHA-256 强校验存在整体缺失静默旁路与 HTTP 非 2xx 误报；状态机推送回调无兜底、启动对账只清"版本相等"、并发检查有双跑窗口、`InstallAsync` 裸 catch；安装脚本（全项目风险最高的 root 路径）零单测；插件在纯浏览器标签页下外链变死链且仍有验收后调试句柄；打包 workflow 空版本会触发 MSB4044 且预览包内嵌版本与 csproj 脱节；版本比较混合段静默补 0。

## Decision

逐项加固（均在 `implemented` 状态描述现有行为）：

- **dev 门禁**：`UpdateOptions.IsEnabledFor(isDev, forceEnv)`——非 dev 恒装载；dev 默认**不装载**自更新栈（命令路由不注册、不启动检查），仅 `DSH_DESKTOP_UPDATE_FORCE=1` 显式开启（升级链路验证用）。装载代码整体包在 `if (updateEnabled)` 内。
- **SHA-256 强校验收口**：`InstallerDownloader` 用 `GetAsync+EnsureSuccessStatusCode`（`GetStreamAsync` 对 404/500 不抛，会把错误页当安装包写满 `.part`）；`ReleaseMeta.Sha256Url` 为 null（release 未附 SHA256SUMS.txt）时**拒绝安装**（宁可误报不装坏包，与条目缺失同立场）；任何失败路径（HTTP/校验/超时）统一删除 `.part` 半成品。
- **状态机加固**：`Transition` 对 `_onTransition` 与订阅者同等待遇（try/catch + 日志）；`StartAsync` 对账改 `Compare <= 0` 清除（降级残留/损坏版本串经 `ShouldClearReady` 一并清除，`Compare` 解析失败视同残留）；`CheckAsync` 判空与占位移入同一临界区（`_checkGate`），并发调用只跑一次检查；`InstallAsync` 的裸 `catch` 改 `catch (Exception)`。
- **安装器可测化**：`InstallCommandFor`/`BuildLinuxScript` 抽为纯函数（包命令、等待环、runuser 降权拉起链、变量透传均为可断言的脚本文本），`LaunchLinux` 只做落盘与 pkexec 启动。
- **插件收敛**：外链取管注册前置 `window.__ryn` 判空（纯浏览器标签页不注册 capture，外链照常）；移除实机验收后的 `window.__ddc` 调试句柄与常驻 `console.info`（同 SPIKE 待遇），保留失败路径 `console.warn`。
- **打包版本**：三平台 `确定打包版本` 步骤在输入留空时回退 csproj `<Version>`（`sed` 提取，版本单一来源），两处都空则 `echo ::error` + `exit 1`（前置 fail loud，杜绝 `-p:Version=` 空值触发 MSB4044）；`workflow_dispatch` 的 `version` 输入默认值由 `0.1.0` 改为留空（预览包不再内嵌与 csproj 脱节的旧版本号）；Linux 的 tag 一致性校验同步放宽为"留空或与 tag 一致均合法"。
- **版本比较**：`UpdateVersion.ParseSegments` 逐段校验，任一段无法解析即抛 `ArgumentException`（混合形态如 `0.a.3` 不再静默补 0）。

## Alternatives considered

- **dev 门禁：仅隐藏按钮 vs 仅禁用 install 命令 vs 不装载整栈**：前两者把 dev 判定泄漏进插件/命令面，且 dev 下"检查"仍有网络动作；整栈不装载最干净——invoke 自然失败、插件静默，FORCE 开关保留验证通道。
- **SHA256SUMS 整体缺失：放行 + 记日志 vs fail loud 拒装**：放行会把"强校验"退化成"尽力而为"，与 ADR 既有立场（宁可误报不装坏包）冲突；选 fail loud，代价是 release 缺校验文件时更新不可用（发布流水线恒附 SHA256SUMS.txt，实际不触发）。
- **对账条件：`== 0` vs `<= 0`**：`== 0` 只覆盖"刚装完"，把**降级残留**（记录版本 < 当前且资产在）漏成降级安装建议；`<= 0` 统一视为残留清除，语义闭合。
- **`_checkGate` 锁 vs `Interlocked` 标记**：锁同时覆盖判空+占位+清理三处，`Interlocked` 只解决计数器、占位清理仍需同步；锁更直白。
- **workflow 版本来源：保留默认 0.1.0 vs 回退 csproj vs `git describe`**：`git describe` 依赖 tag 形态、预览分支无 tag 时退化；回退 csproj `<Version>` 保住"版本单一来源"原则，成本仅一行 `sed`。
- **混合段补 0 vs 抛错**：补 0 掩盖脏数据（`0.a.3` 静默当 `0.0.3`），且 `Compare` 的抛错语义本就存在，收严为"任一段失败即抛"消耗零成本。
- **插件调试句柄：保留 vs 移除**：实机验收已闭环，句柄与 SPIKE 同性质（诊断残留）；移除后诊断走宿主日志 + `console.warn`，不再随包携带可变全局。

## Consequences

- 收益：dev 误装系统包/更新循环根除；完整性最强的校验环节无静默旁路；状态机对账/并发/回调全部闭合；最危险安装路径有内容级回归（24 个新单测，`dotnet test` 102→126/126）；预览包与正式版版本同源；发布流水线对空版本 fail loud。
- 代价：release 缺 SHA256SUMS.txt 时更新整体不可用（接受，防坏包优先）；dev 下验证更新链路需显式 `DSH_DESKTOP_UPDATE_FORCE=1`；workflow 需在输入留空时读 csproj（多一次文件读取，可忽略）。
- 验证：`dotnet test` 126/126、三部门禁全绿、0 警告；client.js `node --check` 与三 workflow YAML 解析通过；`-p:Version=` 空值 MSB4044 已本地复现并在 workflow 前置拦截。

## Related

- `2026-08-22-desktop-shell-self-update`（本加固的上游机制笔记，保持 active，本笔记吸收其"强校验/对账"部分的收严决策并交叉引用）。