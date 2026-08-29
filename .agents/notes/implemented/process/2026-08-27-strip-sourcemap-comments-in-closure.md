# Agent Note: 闭包裁剪剥除 sourceMappingURL 尾注释——交付物不再自相矛盾

Status: implemented

## Problem

随包闭包（`resources/runtime/node_modules`）的 JS/ESM/CJS 产物普遍带 `//# sourceMappingURL=xxx.js.map` 尾注释，而 `trim_runtime_closure` 只删除 `*.map` 文件、不清理注释。注释指向的文件实际不存在：DevTools 打开后按注释去抓 `.map` 全部 404（实测正式版控制台：`index-ClqxG24t.js.map`、`vendor-D22_Mp1f.js.map`、`client.js.map` ×40，聚合 `Source Map loading errors (x44)`）。交付物「带 sourceMappingURL 注释但不带 .map」自相矛盾，调试期控制台被无意义错误刷屏。

## Decision

`trim_runtime_closure` 在删 `*.map` 之后追加一步：对闭包内所有 `.js`/`.mjs`/`.cjs`，删掉行首锚定的 `//# sourceMappingURL=` 注释行（实测命中 `.js` 3950 + `.mjs/.cjs` 355 = 4305 文件）。实现细节：

- **范围**：`grep -rlZ --include='*.js' --include='*.mjs' --include='*.cjs' '^//# sourceMappingURL=' "$DEST/node_modules"` 先列出命中文件（避免全量触碰；.mjs/.cjs 由代码评审实测另有 355 个带悬空注释的 ESM/CJS 产物，如 openai/tanstack 生态），命中非空才执行。
- **执行**：`xargs -0 -n 64 perl -i -ne 'print unless m{^//# sourceMappingURL=}'`。`perl -i` 比 sed 跨平台语法统一（macOS runner 是 BSD sed，`-i` 需显式扩展名）；`-n 64` 显式分批——Windows Git Bash 命令行上限 32K（实测单条路径 ~150B+前缀，200 一批会超），且空输入时 xargs 仍空跑一次 perl 打 stderr warning（`-i used with no filenames`），`[[ -s "$mlist" ]]` 守卫挡掉空列表。
- **锚定一致**：grep 与 perl 均 `^` 行首锚定（评审建议对齐；实测 3950 全为行首、零缩进/零 `//@`/零 CRLF，无实际浪费，对齐仅为防御）。
- **缓存失效由 meta 元数据保证（评审 Blocker 1 修复）**：cache key 虽含 `hashFiles('plugins/**', 'scripts/bundle-runtime-ci.sh')`，但三份 `package-*.yml` 都有前缀式 `restore-keys: dsh-closure-<rid>-`——精确 key miss 后 actions/cache 会部分命中恢复最近旧闭包；旧闭包 `.bundle-meta.json` 若不含裁剪维度，签名校验照过、脚本 `exit 0` 跳过重建，trim 永不执行（v0.3.8「restore-key 回退捡旧闭包」同类洞）。**修复**：meta 增加 `trimPolicy` 维度（`TRIM_POLICY` 正典单点，入库 `printf` 与校验 `grep` 各一处），照 `MARKET_VERSION` 既有先例——本次定义为 `strip-sourcemap-2026-08`。另加 `scriptSha256` 维度（脚本自身 sha256 入库并参与命中校验），脚本任何改动即旧闭包校验失败强制全量重建——「裁剪行为变更必须 bump `TRIM_POLICY`」从注释纪律升级为机器兜底，手工漏 bump 不再能放过脚本变更。

边界：`bundle-runtime.sh`（本地/手动构建路径）未同步此逻辑——其产物保留注释与 .map 原样，本地开发调试不受影响；只有 CI 打包闭包剥注释。`.map` 文件仍删除（体积收益保留）；`client.js.map` 的 40 条 404 中 companion 自身（`plugins/dsh-desktop-companion/client/client.js`）本无注释、不受影响——其来源是上游 `@deepseek-ai/dsh-client-modules`/`dsh-client-runtime` 等构建产物自带的注释，一并覆盖。

## Alternatives considered

- **保留 .map 文件随包**：注释与文件自洽，但亏损闭包裁剪的体积收益（per-arch trim 的既定收益），且上游产物本就大部分不带 .map，无法两头兼顾。
- **仅对已知三个入口（index/vendor/client）剥注释**：覆盖用户实报的三处，但闭包内其余上千文件仍带悬空注释，未来任何被 DevTools 触及的脚本都会复发同样 404；全量剥一次性根治且零成本。
- **改 dsh 伺服层对 .map 请求返回空 200**：属上游行为，不可控且掩盖"注释悬空"这个真问题。
- **文档化让用户关 DevTools source maps**：治标不治本，把产品缺陷转嫁给用户配置，与"交付物自洽"原则相悖。
- **依赖 cache key 的 hashFiles 联动即可、不另加维度（评审 Blocker 1 候选）**：前缀式 restore-keys 会让旧闭包被部分命中恢复、五维签名照过而跳过重建——已实测机制漏洞，落败；须维度入库校验。

## Consequences

- 调试期（`DSH_DEVTOOLS=1`）控制台不再被 `.map` 404 刷屏。
- `trimPolicy` 与 `scriptSha256` 维度为闭包缓存新增失效触点：脚本（含裁剪逻辑）任何改动都会令旧闭包校验失败强制重建；`TRIM_POLICY` bump 仅在需要区分「同脚本内策略语义」时仍有意义。
- 回退线路 `bundle-runtime.sh`（本地/手动构建）未同步此逻辑时不会复发——其产物不剥注释也不删 .map，本就自洽。

### Testing

本地沙箱验证：构造 5 类夹具（带注释 client/index/嵌套 node_modules、无注释 plain、非行首 indent）跑同款命令断言——注释行被剥、业务代码保留、无注释文件未触碰、非行首注释不误删；`bash -n` 语法通过；对真实 `resources/runtime/node_modules` 只读统计：`.js` 3950 + `.mjs/.cjs` 355 = 4305 文件命中、关键目标（dsh-client-modules / dsh-client-runtime / index / vendor）全部在命中集内、grep 与 perl 锚定一致。代码评审（R2）实测复核：`vendor-D22_Mp1f.js` 现存、全树 `.map` 已删、注释存在（404 属实）。

### Related

- [2026-08-20-trim-runtime-closure-per-arch](../simplification/2026-08-20-trim-runtime-closure-per-arch.md)：既有裁剪策略（本决定在其内追加一步）；已同步其「只剪三类」表述为含第四步。
- [2026-08-23-bundle-closure-staleness-and-install-policy-retry](../bug-fix/2026-08-23-bundle-closure-staleness-and-install-policy-retry.md)：`hashFiles` cache-key 事实的权威出处（companionSha256 维度先例）；本决定沿用其机制语义并新增第六维。

- [online-first 去捆绑运行时](../../implemented/architecture/2026-08-29-online-first-unbundled-runtime.md)（implemented）：**本篇宿主机器随其批次二退役**——正文机制仅对存量 tag 包（v0.3.12 及更早）有判别/复现价值，新包无闭包不再产生所述形态。

- 上游待办（无关本仓交付物，记 HANDOFF 待办）：Ryn `LocalWebServer.HandleIpcEvalAsync` 缺 CORS 头（`/ipc/eval/` 响应不带 `Access-Control-Allow-Origin`），与本文档同一实机调试会话发现，拟提上游 PR。