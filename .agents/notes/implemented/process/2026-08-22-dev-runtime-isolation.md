# Agent Note: dev-runtime-isolation

Status: implemented

## Problem

开发运行（`DSH_DESKTOP_RUNTIME_DIR` 指向工作区闭包）时，若已装正式版桌面端正在运行，dev 实例秒退——根因：两者共用同一 ApplicationId，Linux 下 saucer 以 GTK 承载，同 id 的 GApplication 第二实例注册即退出（`NativeAppHost.cs:114` 实证 id 直通 saucer；`ryn.json:identifier` 只是同一配置的文件来源，代码赋值更晚、覆盖有效）。此外 dev 与正式版共享默认 DSH_HOME：两个 dsh 进程共写一个 profile 有数据竞争，`.dsh-web-port` 端口记忆互相覆盖。用户要求 debug 与正式版可并存。

## Decision

dev 判定满足其一即触发（首版仅看环境变量，实测裸跑 `dotnet run` 不设变量时漏网——PATH dsh 回退形态同样需要隔离；[online-first 批次二](../../proposed/architecture/2026-08-29-online-first-unbundled-runtime.md)后第二触发条件由「闭包存在性探测」改为第二个显式标记——判定不探测闭包存在性）：①`DSH_DESKTOP_RUNTIME_DIR` 已设置；②`DSH_DESKTOP_DEV=1` 显式声明。纯判定抽到 `Services/DevEnvironment`（可单测）。隔离内容：

- **ApplicationId 后缀**：dev 加 `.dev`（`io.github.ZK-Andy.dotnet-deepseek-harness-desktop.dev`），Wayland app_id / GTK unique id 随之独立，任务栏出现单独条目属预期。
- **DSH_HOME 自动隔离**：dev 且未显式设置 `DSH_DESKTOP_DSH_HOME` 时，默认指向 `<仓库>/.cache/dev-home`——runtime 目录形态从其两级上溯推导；PATH 回退形态从应用基目录向上找 `.git` 定位仓库根；显式设置仍可指回真实 home。
- **随包插件安装守卫细化**：原「dev 一律跳过」改为「仅当未自动隔离（用户显式覆盖 home）时跳过」——自动隔离的 dev home 与正式版无涉，装上 dshmarket/伴生插件后 debug 功能完整；显式指回共享 home 的场景维持跳过防串扰。

## Alternatives considered

- 只改 ApplicationId、仍共享 DSH_HOME：落败——两个 dsh 进程同跑一个 profile 有会话存储竞争风险，端口记忆互相覆盖（串扰教训的同族问题）。
- 要求 debug 前手动关闭正式版：落败——即本次要解决的痛点本身。
- 给 Ryn/saucer 增加多实例开关：范围外——单实例互斥对最终用户是合理产品行为，只有开发场景需要绕开。

## Consequences

- 收益：debug 与正式版可同时运行；dev 环境完全自包含（端口记忆/updates/插件安装全部隔离），串扰类问题结构性消除；dev home 缺失时首次启动自动初始化全新 profile。
- 代价/风险：dev 首启为空白环境，需重配模型或自行拷贝 `dsh/.credentials.yaml`；任务栏多一个 `.dev` 条目；`.cache/dev-home` 不入 git（已在 .gitignore 覆盖范围内）。
- 附带修复：自更新状态推送在窗口未创建时抛异常拖垮启动检查——`CurrentWindowAccessor.Current` 是抛异常而非返回 null；推送代码补 try/catch，状态机 Transition 对单个订阅者失败隔离。
- 验证：`dotnet test` 87→96→98/98（DevEnvironmentTests 覆盖双触发条件与两种 home 推导）；三门禁全绿、0 警告。实机「正式版开着 + 裸跑 dotnet run 两窗并存」待用户验收。

## Related

- [单实例 launcher 激活](../architecture/2026-08-26-single-instance-launcher-activation.md)：`.dev` 身份后缀规则的第二个消费者——UDS 锁地址同源隔离，开发实例与正式版各持一把锁。
- [online-first 去捆绑运行时](../../proposed/architecture/2026-08-29-online-first-unbundled-runtime.md)（proposed）：**取代本篇判定条件②的原始形态**——「定位不到捆绑闭包」随闭包退役改为 `DSH_DESKTOP_DEV=1` 显式标记（闭包探测会把 online-first 后的全部新装用户误判为 dev，shared-home ADR 在案挂账由其批次二收口）；隔离内容（ApplicationId 后缀 / dev home / 守卫细化）不变。
