# Agent Note: 恢复页对齐 dsh 设计系统——boot 页同款回退调色板

Status: implemented

Review: LIGHT/2026-09-30#1/R2=ok

## Problem

boot 页对齐批（[boot-page-dsh-design-alignment](2026-09-30-boot-page-dsh-design-alignment.md)）明确把恢复页排除在外；用户实机验证 v0.5.12 时发现：插件市场更新重启 dsh 进程 → 健康探针判 Dead → 切出的恢复页仍是旧紫罗兰暗色版（`#0f0f13` 底、`#7c3aed` spinner、`#7c8cff` 状态行），与新 boot 页同屏割裂。壳自有页面至此仅剩这一处异类。

## Decision

[RecoveryPageBuilder](../../../../src/DeepSeek.Harness.Desktop/Recovery/RecoveryPageBuilder.cs) `Skeleton` 内联 CSS 换成 boot 页同款 `--dshdt-*` 回退调色板：

1. **同一套 token**：亮基（官方 boot 页同值）+ `prefers-color-scheme` 暗覆 + `color-scheme: light dark` + `background: Canvas`；恢复页与 boot 页同为「dsh 主题 CSS 必然不在场」的场景，回退策略一致。
2. **官方排版规格**：20px conic-gradient spinner（原 36px 紫边框圈）、16px/600+0.08em wordmark（原 18px 裸 h2）、13px 次级文案。
3. **按钮层级**：导出诊断 = 反色填充主态，退出应用 = 描边幽灵；hover 用 `opacity: .92`（boot 页 R2 S2 同款修正，暗色近白填充有可见反馈）；补 `:disabled` 态（Wire 本就有 disabled 切换，旧版无对应样式）。
4. **状态行降中性**：`#ddc-status` 从紫色改 label-secondary（导出成功/失败同色，文案自明）。
5. **零行为面**：骨架元素 id、Wire 接线、UiCopy 文案、JSON 注入通道全部不动；测试断言（RecoveryPageTests 五例 + PageHealthMonitor 的 `\u003E…\u003C/p\u003E` 形态钉）无颜色钉点，零测试改动。

**调色板重复的取舍**：`--dshdt-*` 值现存在于 index.html 与 RecoveryPageBuilder 两处内联——这是有意为之：恢复页骨架是编译期常量（无外部依赖不漂移），不外链共享 CSS；两处各 ~10 行，漂移风险小于引入共享资源的加载时序面。

## Alternatives considered

- **共享 CSS 文件（wwwroot/boot-tokens.css 双方引用）**：落败——恢复页经 `documentElement.innerHTML` 整体覆写，骨架必须是自足常量；外链 CSS 在覆写时刻的加载时序会引入闪烁与依赖面。
- **C# 侧常量单源（RecoveryPageBuilder 暴露调色板常量、index.html 生成时注入）**：落败——index.html 是静态文件不经 C# 生成，注入意味着新增生成链路，成本远超 10 行重复。
- **等 boot 页批一起发**：落败——用户实机已撞见割裂（v0.5.12 插件市场重启路径），恢复页是崩溃场景的第一屏，独立小批尽快收。

## Consequences

收益：壳自有页面（boot/恢复/companion）视觉语言全线同族，崩溃场景不再闪回旧版。代价：调色板两处内联重复（上文取舍）；spinner 从 36px 缩至 20px（与 boot 页一致，视觉存在感略降）。

## Testing

`dotnet test` 全绿 898/898 零警告（RecoveryPageTests 五例 + PageHealthMonitor 钉点原样通过）；`verify-ui-copy`/`verify-adr-format`/`verify-md-links` 绿；`verify-review-tier --staged` 判 LIGHT；亮/暗两档 Chrome 渲染目检。

## Related

- [boot-page-dsh-design-alignment](2026-09-30-boot-page-dsh-design-alignment.md)（调色板与排版规格来源；其 Alternatives 中「恢复页本批不扩散」由本篇收口）。
- [diag-masking-and-recovery-page](../bug-fix/2026-08-26-diag-masking-and-recovery-page.md)（恢复页职责来源）。
