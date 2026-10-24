# Agent Note: Ryn 残留清账——注释现行态化与死面摘除

Status: implemented

Review: FULL/2026-10-24#3/R1=ok R2=ok R3=ok

## Problem

Avalonia 迁移 G5 下线旧宿主后，全仓审计（用户要求的干净收尾面）扫出三层残留：①本机环境层——`artifacts/publish-linux-x64/` 的 10 个 ryn 产物（`ryn.json`/`libryn-pty.so`/`Ryn.*.dll`）与 `.dotnet-cache/nuget/ryn*` 8 个包缓存（均不入 git）；②词汇层——17 个代码文件与 4 处脚本/忽略配置（smoke 自测夹具、增量闸自测夹具、compose-root docstring、`.gitignore` 退役 saucer 项）约 37 处 Ryn/saucer 时代措辞（含 `LauncherActivation`/`MainUiEntry` 两处 saucer 历史对照），其中 `AppSettingsFileTests` 的注释引用已消亡的 `RynApplicationBuilder` 类型、并保留了「JSON 非法即启动崩」的失效因果（现行装载 fail-safe：回退默认并留痕、不阻塞启动）；③死语义层——`ryn://app` scheme 全仓零产源却仍作为测试夹具出现，`ArchitectureTests` 两处 `Ryn*` 程序集过滤分支永不命中。

## Decision

1. **本机层删除**：artifacts ryn 产物与 nuget ryn 缓存全删（`.dotnet-cache` 非配置 NuGet 源，无还原依赖）。
2. **词汇层现行态化**：注释改写以现行机制为准——顶栏注入是「导航完成 + 顶栏启用」两时机的逐文档求值（`PageCaptionBar.InjectAsync`），非旧注册 API；appsettings 因果链改为「fail-safe 静默丢配置，测试堵静默面」；`ScriptProbeValue.Decode` 表述为「唯一解码实现」（两生产调用点）；假体同步抛出与真适配器 async 链的等价面写明边界。
3. **死语义层摘除**：`ryn://app` 夹具换 `about:blank`（三处被测函数对两 scheme 走同一分类路径，断言语义不变）；`ArchitectureTests` 移除 `Ryn*` 分支（Ryn 包/程序集全仓零引用，分支不可达，收窄为行为保持）；方法名 `ThrowsRynCommandNotFound` 去 Ryn。
4. **脚本层同步现行态**：smoke 自测夹具的 `ryn://app/index.html` 换 `file:///tmp/boot/index.html`（同为非 dsh 源到达，断言面不变）、噪声行类型名换现行壳类型；增量闸自测夹具包名 `Ryn.Plugins.Dialog` 换中性名；compose-root docstring 去 Ryn 词汇；`.gitignore` 删退役的 saucer 会话缓存项（旧宿主已不再生成）。
5. **保留三类合法残留**：`CaptionBarTests`/`RecoveryPageTests` 的 `DoesNotContain("window.__ryn")` 防回潮守卫；ADR 文件名 slug（`avalonia-g5-ryn-host-retirement`）；cookbook/cookbook-archive 历史记录层的 Ryn 字样。`data-webview-*` 属性名不改（活选择器，改名是行为面变更无收益）。

## Alternatives considered

- **整体删除 ryn:// 相关防御测试**：落败——「非同源/未知 scheme 判非 dsh UI」的不变量仍有价值，换中性夹具保留覆盖。
- **ArchitectureTests 连守卫测试一起删**：落败——程序集级互证是 R2 依赖方向的第二级证明，删守卫面大于清残留收益。
- **`data-webview-maximize/minimize` 属性改名去 Ryn 词汇**：落败——属性仍是拖拽排除选择器与静态条 CSS 的活面，改名触发行为面变更与 companion/页面兼容核查，纯命名收益不抵。

## Consequences

- 程序集级互证不再对「未来重引 Ryn 包」报红——该防线归 [avalonia-g5-ryn-host-retirement](../architecture/2026-10-24-avalonia-g5-ryn-host-retirement.md) 与 csproj 评审管辖。
- `CaptionBar.cs:235` 生产注释「窗口背景按平台清成透明」的窗口面机制在本批未核实（src 无 TransparencyLevelHint 落点，【推演 · 未证】机制落在 WebView 包内或未实现）——转后续核实，本批只把测试侧表述收页面口径。
- `AppSettingsFileTests` 的测试价值口径变更：从「堵启动崩」改为「堵丢配置」（装载留痕一行、不拦丢失），测试本体不变；smoke 自测与增量闸自测已随批重跑（脚本变更按流程跑自带自测）。
