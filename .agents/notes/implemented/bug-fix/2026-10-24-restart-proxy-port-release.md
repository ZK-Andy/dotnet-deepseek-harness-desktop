# Agent Note: 重启时先释放回环代理端口

Status: implemented

Review: FULL/2026-10-24#4/R1=ok R2=ok R3=ok

## Problem

`ExitPipeline.Restart` 的次序是「回收运行时 → 释放单实例监听器 → 拉起继任者 → 关窗」，但回环代理的
监听 socket 一直由本进程持有到 `Run` 尾部的 `ShellResources.Dispose`。继任者启动后立即构造代理并试绑
记忆端口（`.dsh-shell-port`）；若它先于那次释放绑定，`DshLoopbackProxy.BindListener` 会把旧进程的
LISTEN 当作「记忆端口被占」，loud 降级为 OS 分配并写回新端口——origin 漂移，页面选中态与依赖 origin
的桌面设置失效一次，会话恢复丢一次。【推断 · 未证】：该序列由 ADR `2026-10-23-avalonia-g3-exit-restart`
的残项记载与 `BindListener` 的降级分支代码路径推得，本次未用重启期 `host.log` 复现。单实例锁与 marker
已按序释放，唯独代理端口没有；旧宿主同形。

## Decision

- **端口释放并入退出管道的既有步骤**：`ExitPipeline` 的构造参数 `disposeListener` 改名
  `releaseLocalPorts`，语义为「本进程持有的本地监听端口」——单实例 UDS 锁（`PrimaryListener.Dispose`）
  与回环代理监听 socket。名字改掉是因为它不再只覆盖监听器。
- **代理新增 `StopListener()`**：只调 `TcpListener.Stop()` 放端口，不动在途连接与转发通道，幂等；完整
  收摊仍归 `Dispose`，由 `Run` 尾部兜底。
- **释放动作落在资源拥有者**：`ShellResources.ReleaseLocalPorts()` = 单实例监听器 `Dispose` +
  `proxy.Proxy?.StopListener()`；`ShellWiringRegistration` 把该方法交给 `AvaloniaHost.CreateLifetime` 作构造数据。
  资源拥有者本来就知道两半是谁，动作因此有直接可测入口，不必藏在装配 lambda 里。
- **释放步改为必填**：`releaseLocalPorts` 去掉可空与「null = 未启用」分支——生产构造点恒有动作
  （句柄为 null 时动作空转），可空只留下一个测试专用的死路径。
- **次序不变**：回收三步 → 释放本地端口 → spawn（重启）/关窗（有序退出）→ 看门狗。

## Alternatives considered

- **spawn 前直接 `ProxySetup.Dispose()` 全量收摊**：会取消受理循环并释放 CTS，而 `ProxySetup.Dispose`
  非幂等（重复 `Cancel` 抛 `ObjectDisposedException`），`Run` 尾部再收一次会炸；在途 SSE 也会被提前掐断。
  落败。
- **把代理对象交给 Core 的 `ExitPipeline` 持有并释放**：Core 会依赖 Infrastructure 的具体类型，破坏
  分层（R3）。落败。
- **让继任者绑定失败时重试等旧进程退出**：时序赌注，且 `BindListener` 的失败语义是「降级 + 重记端口」，
  重试要另造一条绑定路径与超时面。落败。
- **只提前持久化端口、不释放监听**：端口仍被旧进程占着，继任者照样撞上并重分配。落败。

## Consequences

- 重启时继任者在 spawn 后能立刻绑回记忆端口，不再触发「记忆端口被占 → OS 重分配」，会话恢复不再因
  这次释放次序丢掉。
- spawn 到关窗之间，旧进程不再接受新的页面连接（在途连接不受影响）；窗口随即关闭，用户不可见。
- `StopListener` 之后 `Dispose` 仍幂等；异常路径（未走管道）仍由 `ShellResources` 在 `Run` 尾部全量释放。
- **已知边界**：Windows 不设 `SO_REUSEADDR`（ADR
  [shell-proxy-port-persistence](../architecture/2026-09-30-shell-proxy-port-persistence.md) 的既有取舍），
  旧进程在 spawn 到关窗窗口内的已关闭连接若在 `TIME_WAIT`，继任者的首次绑定仍可能失败并漂移一次；本片
  只消除「存活监听」这一类冲突，未在 Windows 真机验证。

## Testing

`DshLoopbackProxyTests.StopListener_ReleasesPortForSuccessor`：同一进程内 `StopListener` 后立刻用新
`TcpListener` 绑回原端口，随后 `Dispose` 幂等收摊；`StopListener_KeepsInFlightConnection`：挂起中的 SSE
在 `StopListener` 后仍收到首行（钉住「只停监听」而非全量收摊）。`ShellResourcesTests`：`ShellResources` 挂上真实 `ProxySetup` 后调 `ReleaseLocalPorts()`，继任者能立刻
绑回代理端口（删掉动作里的 `StopListener` 即红）。`ExitPipelineTests` 的记序用例继续钉住「回收 → 释放端口 → spawn/关窗 → 看门狗」，
释放步已改为必填。
