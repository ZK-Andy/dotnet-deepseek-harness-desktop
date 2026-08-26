# Agent Note: port-memory-per-profile

Status: implemented

## Problem

v0.3.5 自更新实机事故：升级拉起后桌面端反复显示恢复屏（重启循环），直至用户重启电脑才恢复。host.log 实证端口漂移链：新实例按记忆绑定 :46777 成功运行 → 插件装好触发监督器重启 → 重启成功后子进程立刻又退出 → 下次重启落到随机端口 :42859 → 之后两道未正常退出标记与多次手动重开。

根因：端口记忆文件 `<DSH_HOME>/.dsh-web-port` 位于 home 根、跨 profile 全局共享。共享 home 形态下 web 会话（CLI/GUI）与桌面端的 dsh 同住一个 home——两类实例都把「上次端口」当自己的记忆互相抢占：一方在线时另一方按记忆值绑定即撞车秒退。当晚恰逢桌面端自更新与 web 会话连续对话同时发生，争抢循环完整复现；重启电脑清场后才恢复。

## Decision

1. **端口记忆按 profile 隔离**：`ResolvePortFilePath()` 改为 `<home>/profiles/desktop/.dsh-web-port`；web 侧实例不读该文件（上游 dsh 不认识此约定，仅我方壳读写），从此互不影响。
2. **迁移回读零感知**：新位置缺失时回读旧版 home 根文件（`TryLoadPersistedPort` 内置 legacy fallback），存量用户升级后首启端口不变；写入只落新位置，绝不回流旧文件。
3. **诊断包同步**：DiagnosticsExporter 白名单收录新旧两个位置（`state/web-port.txt` + `state/web-port-legacy.txt`），便于事后取证。

## Alternatives considered

- **保持全局记忆 + 增加存活校验（写 PID/owner token 判陈旧）**：落败——能缓解但把简单机制复杂化，且仍允许两类实例在时间窗内互抢；profile 隔离从结构上消除争抢面，无需额外状态。
- **放弃端口记忆，每次 OS 随机分配**：落败——origin 每次变化会丢「冷启动回上次会话」的 localStorage 记忆，为修并发把核心体验一起牺牲。
- **上移到 dsh 上游解决**：文件是我方壳私有约定，上游无此概念；无可上报面。

## Consequences

- 桌面端与 web 会话可长期共存：各自 origin 独立演化，互顶下线消失。
- 存量升级首次启动做 legacy 回读；旧 home 根文件此后成为死数据（诊断包仍收录以便核对）。
- 同 profile 内多开第二实例：多开本身已被单实例仲裁结构性阻止（见 Related）；「占用即回退 OS 分配」兜底保留给孤儿残留场景，回退成功时伴随 host.log 漂移告警（origin 变化与会话选中态后果提示），见 [子进程收割与端口漂移](2026-08-26-child-process-reaping-port-drift.md)。
- 测试 255/255（+3：迁移回读、写入不回流、旧文件损坏容错）；布局契约测试反转为新规格。

## Related

- [持久化 Web 端口跨启动](2026-08-21-persist-web-port-across-launches.md)：端口记忆机制的出处。
- [共享 home 与 desktop profile](../architecture/2026-08-23-shared-home-desktop-profile.md)：共享 home 形态——本修复补上了它此前未暴露的端口争抢面。
- [自更新二代实例环境加固](2026-08-22-self-update-relaunch-env-hardening.md)：同一拉起链路上的前次加固。
- [子进程收割与端口漂移](2026-08-26-child-process-reaping-port-drift.md)：回退兜底的现状触发面与漂移告警所有者。
