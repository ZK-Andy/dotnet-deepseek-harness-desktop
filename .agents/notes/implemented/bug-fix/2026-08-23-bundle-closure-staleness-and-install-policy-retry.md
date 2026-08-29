# Agent Note: bundle-closure-staleness-and-install-policy-retry

Status: implemented

## Problem

v0.2.0 发布暴露两个叠加缺陷，最终表现为「升级后设置页更新入口不出现、外链接管失效」：

① **闭包缓存签名不含随包插件内容**：`dsh-closure-{rid}-{DSH_VERSION}-{NODE_VERSION}-v1` 与 `.bundle-meta.json` 签名只看 dsh/node/platform——本轮仅改 `plugins/dsh-desktop-companion`（0.0.1→0.0.3）时，v0.2.0 tag 构建命中 main 分支旧缓存整步跳过重建（CI 日志实证「闭包缓存命中→跳过重建」），发布包随带 0.0.1 旧 tgz；版本感知升级比对 bundled==installed 后正确地什么都不做。

② **补装管线被 pnpm 供应链政策整体阻断且不可见**：用户 profile 里市场安装的 `dsh-pocket@1.12.3` 发布不足 24h，pnpm `minimumReleaseAge` 政策校验**整份 lockfile** 拒绝任何安装操作——首启补装 companion 的 spawn 以 ERR_PNPM_MINIMUM_RELEASE_AGE_VIOLATION 失败，而 `[host]` 日志只进 stdout（桌面启动形态不可见），机器侧零线索。沙箱以真实 profile 复刻实证：严格模式复现同错；`pnpm_config_minimum_release_age=0` 后安装成功落盘。

## Decision

- **缓存签名纳入插件内容**：`.bundle-meta.json` 增加 `companionSha256`（对 `plugins/dsh-desktop-companion` 源码树确定性哈希），命中判定要求四元组全匹配；三平台 workflow 缓存 key 追加 `-plug-${{ hashFiles('plugins/**', 'scripts/bundle-runtime-ci.sh') }}` 段，从 restore 源头杜绝取回不相容闭包。restore-keys 前缀兜底恢复的旧闭包由脚本内哈希校验二次拦截。
- **政策重试一次**：首次 spawn 保持严格；失败且输出含 `MINIMUM_RELEASE_AGE` 时以 `pnpm_config_minimum_release_age=0` 重试一次并显式留痕——本操作只装第一方 `file:` 包、不新增注册源，放宽面收敛到单一 knob 单次调用。
- **宿主日志落盘**：补装任务全部诊断行 tee 至 `<DSH_HOME>/logs/host.log`（带时间戳）——桌面启动形态 stdout 不可见的问题不再遮蔽机器侧排障。

## Alternatives considered

- **手工把缓存 key 升 v2 了事**：落败——只解本次，下次改插件必复发；hashFiles 段让失效自动化。
- **首次就放宽 minimumReleaseAge**：落败——默认姿态保持严格供应链校验，放宽是针对特定错误码的定向降级且有日志；无差别放宽扩大所有安装的面。
- **插件 tgz 移出闭包缓存、打包期单独 staging**：不采纳——签名覆盖后 staging 留在闭包步骤内即可保证新鲜度，拆出徒增两条装配路径。
- **host.log 加轮转**：暂缓——行级文本增长极慢，先解决可见性；轮转留待真实体积数据。

## Consequences

- 收益：随包插件内容变更必然产出新闭包（脚本与 key 双重保证）；被政策卡住的机器自愈（一次重试）；机器侧排障有持久日志。
- 代价/风险：每次改插件 CI 全量重建闭包（数分钟，预期成本）；政策放宽仅覆盖单次调用的单一 knob；`host.log` 无轮转。
- 验证：`dotnet test` 154/154；`bash -n`/YAML 解析过；沙箱复刻实验证明放宽路径可成功落盘（装的是当时本地 0.0.1 源，属输入问题非机制问题）；v0.2.1 发布后以下载产物实测 tgz 版本为准。

## Related

- [online-first 去捆绑运行时](../../implemented/architecture/2026-08-29-online-first-unbundled-runtime.md)（implemented）：**部分取代本篇**——闭包缓存签名面（Decision 第 1 条与 companionSha256/hashFiles 维度）随其批次二退役；minimumReleaseAge 定向重试与 host.log 落盘仍为现行行为。

- `2026-08-22-companion-plugin-version-aware-upgrade`：比对逻辑本身行为正确（bundled==installed 不动），本次修的是 bundled 输入的新鲜度与 installed 缺失时的可达性。
- `2026-08-22-self-update-relaunch-env-hardening`：同一安装链的上一轮加固（环境/会话归位）。
