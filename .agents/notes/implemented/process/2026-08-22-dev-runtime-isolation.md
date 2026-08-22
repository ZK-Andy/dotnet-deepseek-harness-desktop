# Agent Note: dev-runtime-isolation

Status: implemented

## Problem

开发运行（`DSH_DESKTOP_RUNTIME_DIR` 指向工作区闭包）时，若已装正式版桌面端正在运行，dev 实例秒退——根因：两者共用同一 ApplicationId，Linux 下 saucer 以 GTK 承载，同 id 的 GApplication 第二实例注册即退出（`NativeAppHost.cs:114` 实证 id 直通 saucer；`ryn.json:identifier` 只是同一配置的文件来源，代码赋值更晚、覆盖有效）。此外 dev 与正式版共享默认 DSH_HOME：两个 dsh 进程共写一个 profile 有数据竞争，`.dsh-web-port` 端口记忆互相覆盖。用户要求 debug 与正式版可并存。

## Decision

以 `DSH_DESKTOP_RUNTIME_DIR`（打包产品永不设置）为唯一 dev 标记，触发两项隔离，纯判定抽到 `Services/DevEnvironment`（可单测）：

- **ApplicationId 后缀**：dev 加 `.dev`（`io.github.ZK-Andy.dotnet-deepseek-harness-desktop.dev`），Wayland app_id / GTK unique id 随之独立，任务栏出现单独条目属预期。
- **DSH_HOME 自动隔离**：dev 且未显式设置 `DSH_DESKTOP_DSH_HOME` 时，默认指向 `<仓库>/.cache/dev-home`（从 runtime dir 上溯两级推导）；显式设置仍可指回真实 home。
- **随包插件安装守卫细化**：原「dev 一律跳过」改为「仅当未自动隔离（用户显式覆盖 home）时跳过」——自动隔离的 dev home 与正式版无涉，装上 dshmarket/伴生插件后 debug 功能完整；显式指回共享 home 的场景维持跳过防串扰。

## Alternatives considered

- 只改 ApplicationId、仍共享 DSH_HOME：落败——两个 dsh 进程同跑一个 profile 有会话存储竞争风险，端口记忆互相覆盖（串扰教训的同族问题）。
- 要求 debug 前手动关闭正式版：落败——即本次要解决的痛点本身。
- 给 Ryn/saucer 增加多实例开关：范围外——单实例互斥对最终用户是合理产品行为，只有开发场景需要绕开。

## Consequences

- 收益：debug 与正式版可同时运行；dev 环境完全自包含（端口记忆/updates/插件安装全部隔离），串扰类问题结构性消除；dev home 缺失时首次启动自动初始化全新 profile。
- 代价/风险：dev 首启为空白环境，需重配模型或自行拷贝 `dsh/.credentials.yaml`；任务栏多一个 `.dev` 条目；`.cache/dev-home` 不入 git（已在 .gitignore 覆盖范围内）。
- 验证：`dotnet test` 87→96/96（新增 DevEnvironmentTests 9 例）；三门禁全绿、0 警告。实机「正式版开着 + dev 同时启动两窗并存」待用户验收。
