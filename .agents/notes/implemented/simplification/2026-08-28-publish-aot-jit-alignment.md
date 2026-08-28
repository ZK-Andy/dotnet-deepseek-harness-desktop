# Agent Note: 发布形态对齐——csproj PublishAot=false，AOT 兼容降级为裕度

Status: implemented

## Problem

csproj 声明 `PublishAot=true`，而三条打包流水线（linux/macos/windows）无一例外显式 `-p:PublishAot=false`——发布产物自 v0.1.x 起始终是 JIT。配置与事实长期矛盾：源码大量注释以「AOT 下反射不可用（IL2026/IL3050）」为设计理由，误导维护者把「必须源生成 JSON」当成硬约束，并以为发运产物是 AOT。

## Decision

csproj 改 `<PublishAot>false</PublishAot>`（附注释说明发运现实与理由），三条流水线删除冗余的 `-p:PublishAot=false`（单一事实源归 csproj）；`AppJson` 等注释改写为「发布产物为 JIT，源生成 JSON 保留为 AOT 兼容裕度」。AOT 兼容验证能力保留：需要时可临时 `-p:PublishAot=true` 跑 publish 检查。

## Alternatives considered

- **保持 csproj true、只改注释**：本地默认 `dotnet publish` 与 CI 发运形态继续分叉，每次都要靠流水线参数纠偏，矛盾依旧只是被注释解释。
- **恢复 AOT 发布**：macOS 交叉编需关 AOT（单 runner 矩阵的既定取舍），恢复即放弃该优化；且桌面壳启动/内存收益未经基准页证实，不值当。

## Consequences

- 本地 publish 与发运一致；「源生成 JSON」的动机从硬约束降为裕度（随时可开 AOT），不再阻塞 JsonNode 等合法简化。
- 流水线 publish 命令变短；AOT 兼容成为纯验证性行为。

### Testing

build 0 警告、test 全绿；流水线验证随下次发版 run 观察（publish 表达式仅删参，无新增面）。

### Related

- [2026-08-26-aot-json-source-generation](../bug-fix/2026-08-26-aot-json-source-generation.md)：其立论前提「PublishAot=true、IL2026/IL3050 为硬约束」由本决定降级为裕度——机制（源生成 JSON 通道）照旧，动机降级，决定维持。
- [2026-08-20-linux-packaging-pilot-harness-model](../process/2026-08-20-linux-packaging-pilot-harness-model.md)：同处打包流水线语境的相邻决定（macOS 单 runner 交叉编取舍实录于 package-macos.yml 注释）。
