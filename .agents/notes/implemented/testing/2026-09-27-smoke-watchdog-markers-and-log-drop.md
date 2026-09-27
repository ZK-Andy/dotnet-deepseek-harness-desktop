# Agent Note: 冒烟看门狗只认内容标记与日志落盘

Status: implemented

Review: FULL/2026-09-27/R1=ok R2=ok R3=ok

三审结论：R1 组合根未碰；R2 无跨层新引用（rpm 新增可写挂载仅供日志外带，INNER trap 只写两个固定文件）；R3 外部交互增量收敛测试侧（CI artifact 上传），产品面零改动；D003 无新增空 catch（trap/`|| true` 收口，docker 退出码显式透传）；rpm INNER 循环本地不可跑（容器内），由 Linux CI 腿验证；0 Blocker。

## Problem

run `36316562950` 首个 Windows full-chain 当晚暴露看门狗量错了东西：标记集含 `health|update`，页面健康 alive 与更新检查行周期性出现，给计时器续命——真停滞下它可能永不跳闸（字节版已被杂项清零过一次，run `36310235841`）。另 host.log 只在文件里全：step 日志只有尾巴，超时/停滞复盘只能靠猜（两次 Windows 长等全靠事后 `gh log` 捞）。

## Decision

- 标记集缩到决策与阶段信号（`bootstrap|host|shell|nav`）：`health|update` 出局；自测加杂项不清零用例（Ryn info + update + health 行出现仍判死，`[bootstrap]` 行才复位）。
- 三腿日志落盘：`SMOKE_LOG_DIR`（与 `SMOKE_SHOT_DIR` 同模式，调用方注入）：win/mac 落 stdout 全量 + host.log；deb 腿同款（落盘先于 `rm -rf` 清扫）；rpm 容器腿挂可写目录 + EXIT trap 落日志（未置变量时挂一次性 tmp 并清扫）。
- docker 退出码显式透传（清扫不许覆盖函数状态，调用方 `|| rc_total=1` 语义不变）。
- 三工作流新增 `smoke-log-*` artifact（`if: always()`，与截图同规格）。

## Alternatives considered

- **标记集加更多前缀兜底**：落败——方向反了；集合只应收缩到“能证明进展”的行，加法迟早再引入续命行。
- **日志打进 step 日志全文**：落败——10 分钟全量刷屏淹没信号；artifact 按需取，step 日志保持尾巴 + 心跳。
- **rpm 腿不做（只做三宿主腿）**：落败——rpm 腿在 CI 内真实跑，`--rm` 容器日志无落盘即无尸检；挂载只写两个固定文件，面可控。

## Consequences

下次超时/停滞：artifact 里有 stdout 全量 + host.log 全量 + 截图 + 心跳行，直接定位到阶段，不再捞日志猜。代价：每次冒烟多一个轻量 artifact；rpm 腿多一个条件挂载。

## Testing

- 三冒烟脚本 `--self-test` 全 PASS（含新增 `watchdog-misc-no-reset`）。
- rpm INNER 循环与 trap 本地不可演练（需 docker），随 Linux CI 腿验证；YAML 三文件解析通过。
