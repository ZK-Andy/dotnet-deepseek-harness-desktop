# Agent Note: 架构图重生成——回环代理源入图与 next 通道跟值

Status: implemented

Review: FULL/2026-09-29#2/R1=ok R2=ok R3=ok

## Problem

`docs/architecture.diagram.json` 滞后代码两处（E 批挂账）：回环代理源（`DshLoopbackProxy`，窗口 URL 恒代理源）缺节点，`全局 dsh` 子标仍为 `PATH @alpha`。正文（`docs/architecture.md` 启动模型节、README 工作原理节）早已陈述代理源，图作为视图须跟上。

## Decision

只改 JSON 并重新生成，不手改产物：

1. **新增 `proxy` 节点**（`backend`，`回环代理源 / localhost 页源/转发+隧道`，桌面进程域内）：`shell → proxy`（`页面加载`，窗口 URL 恒代理源）+ `proxy → dsh`（`转发+WS隧道`，emphasis；转发 + WS 裸 TCP 隧道 + `ProxyHeaderPolicy` 自源改写）。spawn + 监督边不动（进程生命周期面，与页内容源面分离）。
2. **`dsh` 子标改 `PATH @next, dsh web`**（跟版通道批已改代码与正文，图随之）。
3. **布局**：proxy 置壳节点正上方（`[210,140]`），页加载边为壳顶→源底垂直线；转发边进 dsh 左侧面；`spawn + 监督` 标用 `labelAt` 钉轨下（端口铺展引入新边后原标漂移压件，诊断修）。
4. **产物**：validate showcase 9/9（repair 3 轮：页加载边定侧 → 转发边进左侧面 → spawn 标钉位）后冻结；deliver 重出 `architecture.html`（642734 字节）；`assets/architecture.svg` 重抽（新内联 SVG + 旧 minimal CSS 原样复用：同 classic 预设且类集合逐项一致；无值属性补 `=""`；xmllint 良构；1440 目检节点/边/标全正确）。
5. **README 双语不动**：中英均引同路径缩略图，内容更新自动跟随，无文案需同步。

## Alternatives considered

- **proxy 并入运行适配节点**：落败——spawn 边是进程生命周期（起/杀/监督），代理边是页内容源（加载/转发/隧道），两面语义不同，并入后"spawn + 监督"与"页面加载"同起点不同义，误导。
- **proxy 置底部（恢复/中继走廊旁）**：落败——底部 y545/572 已有两条虚线走廊，再加节点与长边，交叉难收；顶部左上垂直边零交叉，validate 一次定侧即过。
- **手改 SVG 加框**：落败——产物可再生纪律（diagram-docs ADR），手改在下次 deliver 即被覆盖且无 validate 兜底。
- **本批顺手重画其他漂移**：无——`@alpha` 残留扫描（`grep -rn alpha`：命中仅历史 ADR 正文/测试假数据与自测夹具/`MinimumVersion` 底线值/`release.yml` 预发布正则/已挂账生成物）所见现行面仅此两处。

## Consequences

收益：图与代码就代理源形态重新一致；`@next` 标注全仓（代码/正文/图）收敛。代价：HTML 产物 +2KB（节点一边）；SVG 缩略图 55KB→56KB。visual-check 四档 containment 全过，PNG 证据看过即删不进仓。

## Testing

`validate architecture --quality showcase` 9/9 0 error 0 warning；`deliver` receipt（spec sha256 `8358ccf8…`，artifact `1caad034…`）；`visual-check` pass（Chrome 实测 containment；1440 light 档人眼目检：proxy 节点/两边/spawn 标/legend 正常）；`verify-md-links`（产物路径引用）绿。

## Related

- [architecture-diagram-docs](2026-09-23-architecture-diagram-docs.md)（改图流程本篇执行：改 JSON → validate → deliver → 重抽 SVG → 双语 README 核对）。
- [loopback-forward-proxy](2026-09-27-loopback-forward-proxy.md)（代理源语义来源）。
- [dsh-follow-next-channel](2026-09-29-dsh-follow-next-channel.md)（`@next` 通道来源）。
