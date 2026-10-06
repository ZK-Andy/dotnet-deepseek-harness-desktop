# Agent Note: 配置装载 fail-safe 收口与插件事务符号链接权限降级兜底

Status: implemented

Review: FULL/2026-10-07/R1=ok R2=ok R3=ok

三路首轮各 0 Blocker（11 Suggestion 全部书面裁定，采纳 9/驳回 0/部分采纳 2，修复折入本批）：R1（2S）——Load 半边三抄续收与降级环风险均采纳（前者并入 R2-S3 做 `ConfigSectionJson.LoadFile` 单源，后者由守卫实现吸收）；R2（4S）——环守卫/try 单语句/IOException loud 用例采纳，catch 静默零留痕部分采纳（留痕出口统一进 `LoadFile` 可选 log，启动路径接线 `HostLog.Write`，测试与低层调用保持静默同旧版）；R3（5S）——ADR 补记拆类与指针化、manifest 旧句澄清、页头措辞、2026-10-05 ADR 反向链采纳。三路均在本轮内一次完整返回，无中断。

## Problem

2026-10-06 R1/R2 评审挂账的两处缺陷 + 一处 R2 挂账的 Windows 权限面（本批一并收口）：

1. **配置装载面启动崩**：`RuntimeTimeouts` 与 `UpdateOptions` 的逐键读取用 `JsonElement.GetInt32()`——对小数与超 Int32 范围的值抛 `FormatException`；根非对象（`[]`/`"x"`/`42`/`null`）时 `TryGetProperty` 抛 `InvalidOperationException`。两处 `Load` 只 catch `JsonException`，且都在启动早期调用——一个畸形键即启动崩。`ProxyLogging`（同日 proxy-log-noise-reduction 批）已用 `TryGetInt32` + 根形态防御先行示范，三份 `Load`+`GetInt`/`GetBool` 同形三抄。
2. **符号链接建链无权限 = 升级每次失败**：`PluginProfileTransaction.CopyDirectory` 对每条链接调 `Directory/File.CreateSymbolicLink`，而 `Begin` 拷贝失败整批重抛——Windows 无 `SeCreateSymbolicLinkPrivilege`（非开发者模式/非特权安装）时逐条抛 `UnauthorizedAccessException`，升级在该类机器上每次启动失败，与 2026-10-05 修掉的悬空链卡死（transactional-plugin-copy-preserves-symlinks）同构。

## Decision

1. **通用节装卸器 `ConfigSectionJson`（Core，internal，三工程经既有 `InternalsVisibleTo` 可见）**：`Parse<T>(json, sectionName, parseSection, fallback)` 收口「全文解析 → 根/节形态防御 → 抽节回调」——根非对象/无节/节非对象一律回默认，`JsonDocument` 生命周期由回调窗口兜住（`JsonElement` 是 doc 视图，回调外即悬垂）；`GetInt`（`TryGetInt32` + 下界参数，小数/越界/小于下界回默认）与 `GetBool` 同源；Load 半边单源在 Infrastructure 侧 `ConfigSectionFile.LoadFile<T>`（读文件 + 加宽 catch + 失败经可选 `log` 留痕，null = 静默同旧版；文件 IO 不进 Core——pre-commit D005 实拦后的落位修正）。三份选项（`RuntimeTimeouts`/`UpdateOptions`/`ProxyLogging`）的 `Parse` 改为薄壳委派、`Load` 改为 `LoadFile` 透传；九个启动路径调用点（组合根/页面泵/版本门/接力激活/代理/血统/插件探针/首启引导）接线 `HostLog.Write`——appsettings 被锁/权限滑失时操作者可从 host.log 得知配置被忽略。fail loud 与 fail-safe 的分界保持在 IO 边界（纯函数 `Parse` 除坏 JSON 外不抛的契约不变）。
2. **建链无权限降级**：拷贝机械自 `PluginProfileTransaction` 抽至新类 `PluginStagingCopier`（internal static，事务本体只留 journal/recover 编排——F1 尺寸闸要求先拆再上），`RecreateLink` 对两个 `CreateSymbolicLink` 调用点 catch `UnauthorizedAccessException`（仅权限面；`IOException` 等真实异常仍 fail loud）降级——目录链穿链复制内容（嵌套链接走同一路径）、有效文件链 `File.Copy` 跟随复制、悬空链跳过，每条路径恰留一行日志；降级穿链遍历带 **环守卫**（visited 集合记已解析目标目录全路径，命中即跳过防 pnpm 互指环图无界递归）；`CopyDirectory` 返回降级条目数、`Begin` 汇总 loud 一行（含 Windows 缺 `SeCreateSymbolicLinkPrivilege` 指向）。降级语义抽 internal（`DegradeDirLink`/`DegradeFileLink`/`RecreateLink`）直测——真实触发面无法在 POSIX CI 注入。降级只影响 staging 布局保真度（node_modules 链接布局变实体副本），升级可完成；staged `dsh plugin add` 重整后收敛。
3. **cookbook 预算腾挪（挂账②）**：两条退役条目（release「等待产物」空转轮询——等待层已随三流合一删除；SFX 安装器——NSIS/SFX 回退链已删）迁冷归档层，MSYS2 转换课压瘦并入主档「Windows 打包三连坑」③；删两条与 AGENTS.md 单一家重复的指针条目（doc-budgets manifest 命令、HANDOFF/.plan 本地文档），「沙箱 dotnet 缓存」条目改为指向 development.md 的一行指针（操作步骤单一事实源在彼）；欠账两笔归位主档——「注册点闸红的判别」（coupling-shift-left-gates）与 bash-3.2 全角字节课（并入跨平台 shell 语法坑 ⑥，源 ADR 为 2026-09-27-auth-replay-diagnostic-and-mac-ci-hygiene）；归档层提额 300→400 附 `_justify_bump`，其「冻结」语义澄清为「迁入允许归并改写、落定后不改，不承载在役条目」。

## Alternatives considered

- **配置面维持各写各的（只在两份里抄 `ProxyLogging` 的修法）**：落败——三份同形第三抄，正是 coupling-shift-left-gates 要治的耦合形态；装卸器是收口不是新增抽象。
- **装卸器下放 Infrastructure**：落败——`UpdateOptions` 在 Core，依赖方向不允许 Core 引 Infrastructure；放 Core（对 Infrastructure 已开 `InternalsVisibleTo`）是唯一单向位。
- **降级面向所有建链异常（连 `IOException` 一起吞）**：落败——`IOException` 是「staging 目标已存在」类真实异常，吞掉即把真缺陷伪装成布局偏差；只降级权限面，其余照旧炸出来。
- **无权限时整体 fail loud 不降级**：落败——权限面是机器配置不是数据损坏，用户侧修法（开开发者模式/提权）不可发现；降级让升级可完成，代价（staging 布局偏差）被重整吸收，与「悬空链跳过」先例同一取舍方向。
- **归档层提额不写理由**：落败——manifest 判据要求 `_justify_bump`；且「只减不增」语义需随首次迁入澄清，否则门禁文本与实操矛盾。

## Consequences

- 买到的：配置任一键畸形（小数/越界/根非对象/文件不可读）不再启动崩，回默认值与缺省同途且失败原因留痕 host.log；三份装卸面单源，第四份选项记录零新增防御代码；无符号链接权限的 Windows 实机升级从「每次失败」变「可完成 + loud 留痕」，且环守卫保证互指链接图不会把降级变成栈溢出；cookbook 欠账清零，主档恢复 2 词余量。
- 付出的：降级路径的 staging 与 active 结构有偏差（链接布局→实体副本），pnpm store 大目录会被复制放大——仅发生在无权限机器，属可完成性换保真度的明码取舍；`ConfigSectionJson.GetInt` 的下界参数目前仅 `ProxyLogging` 使用，其余键无钳制（与既有「无钳制」哲学一致）。
- 归档层首次迁入，其页头冻结语义同步澄清；archived 源 ADR（2026-09-27-auth-replay）的「课暂寄」表述成为历史记录，不回改。

## Testing

- `dotnet build` 0 警告；`dotnet test` 975/975（基线 960 + 新增 15：根非对象×4 与不可表示数值各进 `RuntimeTimeouts`/`UpdateOptions`，事务降级缝 3 例 + 环守卫/IOException loud 2 例——目录链穿链树复制/有效文件链复制/悬空跳过留痕）；`scripts/test-baseline.json` 与 README 双语徽章同步 973/973。
- 门禁：doc-budgets（cookbook 2759/2761、archive 387/400）、cookbook、adr-format、md-links、code-health、code-conventions、compose-root、registration-discipline、ui-copy、governance、shell-standards、actionlint、readme-badges、handoff-structure 全绿。
- 配置装载的降级行为（`Load` 遇畸形文件回默认）由既有 `Load_BrokenJson_FailsSafeToDefaults` 家族与新增纯函数用例共同钉住；权限降级真实触发面（Windows 无特权）留待实机取证，判据 = host.log 出现「无符号链接建链权限」汇总行且升级走通。

## Related

- [transactional-plugin-copy-preserves-symlinks](2026-10-05-transactional-plugin-copy-preserves-symlinks.md)——链接重建本体语义与 `File.Exists` lstat 陷阱（本篇在其上补权限降级面）。
- [proxy-log-noise-reduction](../../implemented/architecture/2026-10-06-proxy-log-noise-reduction.md)——`ProxyLogging` 的 fail-safe 装载先例与本篇装卸器的同款哲学。
- [coupling-shift-left-gates](../process/2026-10-01-coupling-shift-left-gates.md)——注册点闸（本篇 cookbook 补记的判别条目来源）。
- [cookbook-retired-entry-cold-archive](../process/2026-09-27-cookbook-retired-entry-cold-archive.md)——冷归档层判据（本篇首次执行「迁入」动作）。
