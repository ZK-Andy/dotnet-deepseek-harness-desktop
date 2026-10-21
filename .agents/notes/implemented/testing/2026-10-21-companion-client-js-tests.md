# Agent Note: companion 客户端 JS 测试腿（node:test + 沙箱假件）

Status: implemented

中文（双语暂不启用；启用时恢复 .md 与 .zh.md 镜像 + .i18n.yaml 配对机制）

Review: FULL/2026-10-21#2/R1=ok R2=ok R3=ok
Review: FULL/2026-10-21#3/R1=ok R2=ok R3=ok

## Problem

`plugins/dsh-desktop-companion/client/client.js`（856 行）承载桌面设置页的三块 UI（自更新 / 诊断 / 桌面），外加托盘事件中继、locale 桥与外部链接失败 toast，是用户在设置页操作壳能力的主入口（托盘另有独立入口）；但它没有任何 JS 测试工程，CI 只跑 .NET 腿。审计把这条记为「补需先定运行器与 CI 接法」，因为仓内此前没有 JS 工具链：没有根 `package.json`、没有 bundler，插件本身也不构建（`client.js` 原样进 tgz）。

可测性的真实约束是形态而非缺工具：该文件是经典脚本，只经 `window.__ModuleLoader__.load({id, factory})` 注册，`factory` 收到的 `require` 只能解析模块表里的包名（不能相对导入）——所以「抽纯函数到单独模块再 `import`」这条路不成立，测试必须按浏览器装载方式驱动它。

## Decision

运行器用 Node 内置 `node:test`（零 npm 依赖，CI 无需 `pnpm install`）；测试与被测文件同包、但 `plugins/dsh-desktop-companion/test/` 与 `plugins/dsh-desktop-companion/testkit/` 都不进 tgz（`build-companion-tgz.sh` 只收 `package.json`/`cordis.patch.yml`/`lib`/`client`）。

装载方式与 web 壳一致：`node:vm` 沙箱提供 `window.__ModuleLoader__`（捕获注册记录）、`document`、`MutationObserver`、可控 `setTimeout`；取出记录的 `factory`，以假 `require`（react + 可选 ui-primitives）materialize，再对假 Cordis 上下文调 `apply(ctx)`。`testkit/harness.js`（测试配套，不在 `test/` 下，见 [CI 命令口径](../bug-fix/2026-10-21-companion-js-ci-node22.md)）提供三件测试配套：

- **DOM 桩**：只覆盖插件实际用到的面——`createElement`/`append`/`getElementById`/`addEventListener`/`classList`/`textContent`/`html[lang]` 属性观察。
- **可控时钟**：`setTimeout` 只在测试推进时间时触发（重试 800ms、命令超时 4s、toast 5s 都可确定驱动）。
- **React 桩 + 极简 hooks**：`createElement` 即时展开函数组件，`useState`/`useEffect` 按组件函数保存状态与依赖，显式重渲染即观察 setState 后的树。

用例 18 条覆盖：模块注册契约（id/factory/apply+inject）、两个 slot 的 id/order/locale 与中英文 label、托盘事件哑中继、locale 桥（初始上报与 `html[lang]` 变化）、侧栏更新按钮四态（隐藏/ready 显示版本/installing 菊花/推送事件驱动）、设置页更新块五态文案与动作（checking/downloading/ready/uptodate/error）、`nostack` 与 `channel` 两条不可用提示的判别与降级上报、诊断导出成功/失败、关到托盘 `available=false` 禁用、开机自启乐观切换与失败回滚、重启行失败回显、外链失败 toast 与 5s 自动收起。无宿主桥（无 `window.__ryn`）时 `setupUpdateUI` 早退：整页 UI 不注册且不抛（web boot 不被插件拖垮）由用例钉住。另加 2 条布局自检用例（`test/layout.test.js`），钉住本仓的布局政策——`test/` 树内只允许常规的 `*.test.js` 文件（递归，符号链接判违规）、`test/` 之外不得有 `*.test.js` 文件、插件内只有一个名为 `test` 的目录——把配套件误放导致的用例计数漂移挡在 CI 之前（见 [CI 命令口径](../bug-fix/2026-10-21-companion-js-ci-node22.md)）。

CI 新增无条件 job `companion-js`（`actions/setup-node@v4`，Node 22，在 `plugins/dsh-desktop-companion/` 下跑 `node --test`——命令形态与踩坑见 [CI 命令口径](../bug-fix/2026-10-21-companion-js-ci-node22.md)）；本地跑法记在 `docs/testing.md`（同样在插件目录下跑 `node --test`），不另设 `package.json` 脚本，避免命令字面出现第二个家。job 无条件触发（与 `docs` job 同因：插件改动不命中 `code` 过滤面，且整条腿只有秒级成本）。

## Alternatives considered

- **jsdom / happy-dom + 真 React + React Testing Library**：落败——需要引入 npm 依赖与安装步骤，而本仓没有 JS 工具链；本插件断言的观测面是「渲染出的文本/属性 + 发往宿主的命令」，不是浏览器布局或 React 内部语义，真 DOM 换不来对应的检出增益。
- **vitest / jest**：落败——同类依赖成本；`node:test` 已覆盖需要的断言、并发与过滤能力。
- **把纯逻辑抽到 `.mjs` 供 `import`**：落败——`client.js` 的模块表 `require` 不支持相对导入，抽文件后浏览器入口无法再引用它（要引入 bundler 与构建步骤，等于给一个「原样进包」的插件加工具链）。
- **在真浏览器里以 `__ModuleLoader__` 装载做 e2e**：落败——CI 无浏览器会话，且这条腿要覆盖的是命令流量与状态机文案，不是像素。
- **只测纯函数（parseFrame/statusText）而跳过组件**：落败——它们都在 `apply` 闭包内不可导出，且真正会坏的正是「哪个状态显示哪句话、点击发哪条命令」。

## Consequences

- 收益：插件 UI 的状态机文案、按钮命令、乐观回滚、降级上报进入自动化；CI 每次 push/PR 都执行，秒级成本；零 npm 依赖，不改变打包形态（`test/`、`testkit/` 不进 tgz）。
- 代价：假 DOM/hooks 与真 React 存在语义差（例如 effect 时机、批量更新），断言只能停在「渲染结果 + 命令流量」层；组件若引入新的 DOM API（如 `querySelector`、`focus()`）需要同步扩桩，缺桩会以 TypeError 立刻暴露而不是静默。
- 边界：CSS/布局、样式令牌、真实浏览器事件时序不在覆盖面，仍由发版冒烟与实机验收兜底。
- 新的 JS 腿不进入 .NET 用例计数基线（`scripts/test-baseline.json` 只统计 `dotnet test`），README 徽章口径不变。

## Testing

- `node --test`（在 `plugins/dsh-desktop-companion/` 下）：20/20 通过（18 条行为用例 + 2 条布局自检）；实测 Node v20.19.0 / v22.23.3（CI 钉的版本）/ v26.8.1 三个点的同一形态均通过（`node:test` 在 Node 18 为实验特性、Node 20 起稳定）。
- CI：新增 `companion-js` job 在 `ubuntu-latest` 上跑同一条命令，与 `code` 过滤面无关（插件改动也执行）。
- 与 .NET 腿的关系：两侧都绿才算本批完成；JS 腿失败不阻塞 `build-test` 的启动（独立 job），但整条 workflow 结论为红。

## Related

- [CI 命令口径](../bug-fix/2026-10-21-companion-js-ci-node22.md)：本篇 JS 腿的 CI 跑法（目录位置参数的行为随 Node 版本变化，附实测矩阵）。
- [companion 设置页三块合一](../bug-fix/2026-08-24-companion-settings-consolidation.md)：设置页三块合一（桌面设置/诊断/桌面）的来源。
- [窗口脚本求值端口](../architecture/2026-10-21-window-script-target-port.md)：同批的 .NET 侧测试面补洞。
- [插件打包脚本](../../../../scripts/build-companion-tgz.sh)：`test/`、`testkit/` 不进包的结构性保证。
