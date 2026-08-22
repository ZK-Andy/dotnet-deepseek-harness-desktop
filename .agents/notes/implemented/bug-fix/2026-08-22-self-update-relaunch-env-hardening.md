# Agent Note: self-update-relaunch-env-hardening

Status: implemented

## Problem

自更新实机验收沉淀的两个已知小项：

① **重启实例的 saucer/libsoup 状态目录解析到 /root**（警告、功能正常）。二代实例 = pkexec(root) → install.sh → `runuser -u <原用户> -- env …` 拉起。旧脚本的 env 块对全部变量**无条件**写 `$VAR` 引用：pkexec 未注入的变量以**空串**到达二代实例——「未设置」≠「空串」，glib/libsoup 的 XDG 路径解析被空串顶掉默认逻辑即可错乱（/root 症状属此类环境不完整，调试期中间版本连 HOME 都不在透传清单）；且 XDG_DATA_HOME/CONFIG/CACHE/STATE、USER/LOGNAME/SHELL 从未透传，用户自定义过基目录时二代实例静默回退 HOME 派生路径；nohup 还继承 pkexec 脚本的不可控工作目录。

② **dev 循环二代实例无终端时 polkit 可能拒认证**。二代进程由 root 脚本经 runuser 派生，不在原 logind 会话 cgroup 内——它再触发自更新时，polkit 按进程归属找不到活动会话，不弹授权框直接拒绝。

## Decision

重写 `BuildLinuxScript`/`LaunchLinux` 的环境透传与拉起方式：

- **透传 = 生成期收集非空值 → 字面量单引号对**：两处共用同一 env 字典，清单扩入 XDG 四基目录 + USER/LOGNAME/SHELL；空值绝不写入（空串覆盖比缺失更有害），`DSH_DESKTOP_DSH_HOME` 仅在非空时出现。
- **拉起前 `cd '<HOME|/>'`**：二代实例工作目录确定性，不再继承脚本 cwd。
- **二代实例优先包一层 `systemd-run --user --scope`**（有 `DBUS_SESSION_BUS_ADDRESS` 且 systemd-run 可用时）：进程归位用户管理器作用域，logind/polkit 能按会话归属弹授权；条件不满足时维持原降权直拉兜底。

## Alternatives considered

- **仅文档记录、暂不改代码**：落败——空串覆盖与 cwd 继承是真实潜伏故障类，纯函数修复成本极低且可内容级锁定。
- **`runuser -l`（登录 shell 初始化整套环境）**：落败——它会重置全组环境变量，把刚透传的 DISPLAY/DOTNET_ROOT/DSH 隔离一并洗掉，等于复归实机教训④。
- **gen-2 用 `systemd-run --user` 长驻 unit（--unit 服务化）替代 scope**：不采纳——scope 已解决会话归属，unit 化额外引入卸载/重启的生命周期管理负担。
- **polkit 规则放行或 setuid helper**：落败——放松安全边界换取便利，方向性错误；正确思路是把进程放回应在的会话位置，而非绕过检查。

## Consequences

- 收益：二代实例环境完整性成为生成期保证（内容级测试锁定，不再依赖实机踩坑发现）；连续多轮自更新（升完再升）在无终端场景也能正常弹授权；XDG 基目录自定义用户的壳状态不再串位。
- 代价/风险：systemd-run 分支依赖用户总线存活（图形会话下必然存在，且分支自带无总线兜底）；脚本内容契约变化已同步内容级回归；**真机闭环验证需下次发版后完整走一轮「升级 → 二代实例 → 再触发检查」**。
- 验证：`dotnet test` 152→153/153（透传链/空值省略/引号转义/空字典收敛等 4 组内容级断言更新+新增）；沙箱无 polkit/systemd 会话，运行时行为待实机复核。

## Related

- `implemented/process/2026-08-22-desktop-shell-self-update`：安装链本体与四坑沉淀——本笔记的 ①② 即其「已知小项」两条的收口。
- `implemented/feature/2026-08-22-companion-update-settings-section`：error message 外发使这类授权失败首次能对用户可见。
