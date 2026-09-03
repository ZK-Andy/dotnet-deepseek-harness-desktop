# Agent Note: nativeaot-probe-after-composition-refactor（组合根重构后做 NativeAOT 兼容性探针）

Status: proposed

## Problem

项目当前 `<PublishAot>false</PublishAot>`，三条打包流水线（linux/macos/windows）发运产物自 v0.1.x 起一直是 JIT（csproj 显式声明，ADR `publish-aot-jit-alignment` 对齐发运现实）。NativeAOT 此前被两次决策排除：
- `publish-aot-jit-alignment`（2026-08-28）：csproj 对齐发运现实，AOT 兼容降级为"裕度"。
- `installer-size-retain-self-contained`（2026-08-30）：为降安装器体积否决 AOT——Ryn 依赖反射/服务注册/DI 面，AOT 兼容高风险，macOS 交叉编需关 AOT（CI 结构代价），"成本高、回报不确定"。

但 NativeAOT 的收益面（免运行时随包 → 体积 88M 级下降、冷启动更快、常驻内存更低）恰好撞上两项在途关切：轻量基准页（冷启动/内存指标）与安装器体积。.NET 10 的 AOT 成熟度亦高于当初评估时点。2026-09-03 用户拍板：**重构完成后做一次 AOT 测试**——以探针数据而非推测重开此决策。

## Proposal

在**组合根阶段方法重构 + 组合根 TODO 收口落地后**（避免与核心时序改动交织），执行一次本地 AOT 兼容性探针：

1. 本地（Linux）`dotnet publish -r linux-x64 -p:PublishAot=true -p:Version=<当前>`，观察能否产出、收集 IL2026/IL3050 裁剪警告清单（重点 Ryn/saucer/DI/反射面）。
2. 产物可跑性验证：能跑多少算多少（`--export-diagnostics` + 壳启动；沙箱受限则转交真机）。
3. 记录对比数据：AOT 产物体积 vs 当前 JIT 自包含（~88M 落盘 / 安装器 25-39M）；启动/内存若有测量能力则记。
4. 拿数据再拍板：兼容 → 评估拆 mac 流水线（交叉编 vs 关 AOT）与 CI 构建时间成本；不兼容 → 记录卡点清单（需 `[DynamicDependency]`/rd.xml 补的反射面），仍走 JIT，更新既有 ADR 的"裕度"定位。

**探针为只读验证行为**，不改变发运形态；结论（开/不开/部分开）须另立决策，不随探针夹带。

## Alternatives considered

- **现在（重构前）就全量切 AOT**：核心时序尚未重构，若 AOT 又不兼容，两套高风险变更叠加，问题难归因。落败——探针排在重构后。
- **完全不碰 AOT**：错过潜在高收益（体积/启动/内存三收益，撞基准页指标），且"裕度"定位永远停留在纸面。落败（用户拍板：重构后测一次）。
- **只在文档层面重申"维持 JIT"**：无数据支撑的重复结论，未回答 .NET 10 下兼容性是否已改善。落败。

## Acceptance criteria

- 探针执行并产出：裁剪警告清单 + 产物可跑性 + 体积对比（AOT vs JIT）。
- 基于数据形成明确结论（开 / 不开 / 条件开），并落 ADR 更新或新立决策。
- 探针不改变发运形态；组合根重构与 TODO 收口先行落地。

## Risks

- 探针发现不兼容，需投入逐点补反射标注（`[DynamicDependency]`/rd.xml），成本不确定——缓解：探针先行，数据不足以支撑投入则不投。
- Ryn/saucer 原生桥在 AOT 下行为差异（若产物可跑但运行期异常），沙箱验证力有限——缓解：转交真机清单。
- mac 交叉编与 AOT 冲突（若决定开），CI 三平台结构需重构——缓解：单独立项评估，不在探针内解决。

## Related

- [publish-aot-jit-alignment](../../implemented/simplification/2026-08-28-publish-aot-jit-alignment.md)（implemented）：csproj `PublishAot=false` 对齐发运，AOT 兼容降为裕度的出处；本探针若开 AOT 需翻案。
- [installer-size-retain-self-contained](../../implemented/architecture/2026-08-30-installer-size-retain-self-contained.md)（implemented）：否决 AOT 降体积的决策；本探针以数据重开。
- [aot-json-source-generation](../../implemented/bug-fix/2026-08-26-aot-json-source-generation.md)（implemented）：源生成 JSON 通道（AOT 兼容裕度的机制基础），探针时验证其是否覆盖裁剪面。
- 待办区（`HANDOFF-todos.md`，本地 gitignore 行动文档）「组合根阶段方法重构」「组合根 TODO 收口」：本探针的执行前置。
