#!/usr/bin/env bash
# smoke-wait-lib.sh — 冒烟等待与落定共享库（唯一家：判定串①②、窗口解析、等①、等落定、
# 心跳、无进展看门狗、回退门）。消费方：linux/mac/win 三冒烟脚本与 rpm 容器腿（挂载后 source）。
# 依赖：common.sh（消息模板）——本库自带 source，消费方只需 source 本文件。
# 本文件只含定义，source 无副作用（窗口解析在函数里，见 smoke_resolve_windows）；
# 调用方须已 `set -euo pipefail`（容器侧 `set -uo pipefail`）；OUT/LOG/rc 由调用方设置。
# shellcheck disable=SC2034  # 判定串/正则常量由调用方与自测消费

_SMOKE_WAIT_LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=./common.sh
source "$_SMOKE_WAIT_LIB_DIR/common.sh"

# 判定信号（双信号，唯一家）：①`[host] dsh web =`（注意是等号——`dsh web:` 冒号格式是 dsh
# 子进程自检输出，壳打印的是等号格式；两串曾因错位致冒烟恒败，CI 实证）= dsh 就绪（传输层）；
# ②`[bootstrap] 引导开始：` = 安装链保底（装包→依赖齐→运行时检测→首启引导已启动）。
# 与 NAV/MINT 正则同置库内：各腿不再各写一份、容器腿经挂载共享库同样拿到（无需 `-e` 注入）。
FULL_RE='\[host\] dsh web ='
BOOT_RE='\[bootstrap\] 引导开始：'

# 导航到达行：只作诊断回显（FAIL 证据打印），不判门——新链路零 host 导航，
# holder 自 reload 不产生到达回调（dispatch 36300876224 双腿实证：代理流量
# 证明 reload 发生，到达计数 0 新增）。token 第二跳已随转发模型退役
# （token 永不进导航靶点，见 DesktopBootstrap.App 注释），此处不再检查。
NAV_RE='\[nav\] 导航已到达'
# 铸币行（ADR loopback-forward-proxy）：`MintAsync` 成功即打印，
# `token 跳 → 303` 是转发路由存在的唯一机器可读信号。
MINT_RE='\[shell\] 铸币：token 跳 → 303'
# 终页裁决（ADR page-verdict-gate）：应用在落定期写下唯一的裁决行；显示腿的"绿"必须由 healthy 背书。
PAGE_LINE_RE='\[nav\] 页面裁决=(healthy|auth|unknown)'

# 等待窗/落定窗的唯一家。等待窗 = 单轮尝试预算：RuntimeBootstrapOptions.StepTimeoutMinutes
# （默认 10 分钟）单步上限 + 120s 余量 = 720s；重试轮不计入——冒烟只等首轮落定，超时按②收工。
# 引导步数或 StepTimeoutMinutes 变化时必须同批重算。落定窗 = ①出现后等铸币 + 客户端存活
# （auth 裁决即拦）。SMOKE_WAIT_SECONDS / SMOKE_SETTLE_SECONDS 是覆写旋钮，SMOKE_WAIT /
# SETTLE_WAIT 是库读的全局：调用方 source 后**必须先调本函数**——漏调会让
# `seq 1 "$SMOKE_WAIT"` 在 set -u 下直接炸（rpm 容器腿改名时实测的回归形态）。
smoke_resolve_windows() {
  SMOKE_WAIT="${SMOKE_WAIT_SECONDS:-720}"
  SETTLE_WAIT="${SMOKE_SETTLE_SECONDS:-90}"
}

# 最后一条页面裁决（healthy/auth/unknown）；无则空串。取"最后一条"而非"曾经命中"：
# 每进程今日至多一条裁决行，但落定/重试形态一旦增多，旧 healthy 不得冒充绿（防御性取尾）。
# 读写约定：$OUT 优先、$LOG 兜底（两文件同流镜像且 HostLog 先写 stdout，置位腿单文件即精确，
# 宿主腿兜底不失序）。
page_verdict_state() {
  local last=""
  if [[ -n "${OUT:-}" && -f "${OUT:-}" ]]; then
    last="$(grep -hoE "$PAGE_LINE_RE" "$OUT" 2>/dev/null | tail -n 1 || true)"
  fi
  if [[ -z "$last" && -n "${LOG:-}" && -f "${LOG:-}" ]]; then
    last="$(grep -hoE "$PAGE_LINE_RE" "$LOG" 2>/dev/null | tail -n 1 || true)"
  fi
  printf '%s' "${last##*=}"
}

# 双源日志探活：stdout 或 host.log 任一命中 $1（调用方定义 OUT/LOG 全局）。
log_has() { # $1=正则
  grep -qE "$1" "${OUT:-}" 2>/dev/null || { [[ -n "${LOG:-}" && -f "$LOG" ]] && grep -qE "$1" "$LOG"; }
}

# 双源时间戳归一化：host.log 行带 `[yyyy-MM-dd HH:mm:ss] ` 前缀，stdout 无；
# 去重前先剥，否则同一行被数两次（曾致"到达 2 次"误导三轮，见 R6）。无前缀行原样透传。
strip_ts() { sed -E 's/^\[[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}\] //'; }

# 导航到达行（OUT 与 host.log 双写，去重防双计；任一缺失即跳过该源）。
# pipefail 注意：组内 grep 读空文件/无命中即退 1，组状态会被 pipefail 透过调用方
# 管道传出（曾致命中仍判失败）——组尾 `|| true` 把组状态恒置 0，命中与否只由外层 grep 判定。
nav_lines() {
  { [[ -n "${OUT:-}" ]] && grep -hE "$NAV_RE" "$OUT" 2>/dev/null; [[ -n "${LOG:-}" && -f "$LOG" ]] && grep -hE "$NAV_RE" "$LOG" 2>/dev/null; true; } | strip_ts | sort -u
}
nav_count() { nav_lines | grep -c . || true; }

# 铸币行（双源同上，去重防双计）。pipefail 收口同 nav_lines。
mint_lines() {
  { [[ -n "${OUT:-}" ]] && grep -hE "$MINT_RE" "$OUT" 2>/dev/null; [[ -n "${LOG:-}" && -f "$LOG" ]] && grep -hE "$MINT_RE" "$LOG" 2>/dev/null; true; } | strip_ts | sort -u
}
# 命中判定读完全部输入再退（不用 -q：-q 命中即关管道，上游 sort 收 SIGPIPE，
# pipefail 下同样误报；>/dev/null 等价静默且无此风险）。
mint_seen() { mint_lines | grep -E '.' >/dev/null; }

# FAIL 证据显式打印（诊断用，不判门）：到达/铸币去重行 + token 脱敏。
# 失败分支只打 tail 会被代理行为日志淹没关键行，此处专打判定信号（见 R6）。
echo_nav_lines() {
  { nav_lines 2>/dev/null; true; } | sed -E 's/token=[^& ]*/token=***/g' | sort -u >&2 || true
}
echo_mint_lines() {
  { mint_lines 2>/dev/null; true; } | sort -u >&2 || true
}

# 缺信号说明（落定失败/退出的诊断后缀）：逐项点名缺①、缺铸币 303、存活不足。
settle_missing() {
  local miss=()
  log_has "$FULL_RE" || miss+=("缺①[dsh-web]")
  mint_seen || miss+=("缺铸币303")
  smoke_client_alive "${LOG:-}" || miss+=("存活不足")
  printf '%s' "${miss[*]:-全齐}"
}

# 落定等待（ADR shell-settle-behavior-gate）：新链路零 host 导航，落定 = 传输 +
# 铸币 + 行为三信号——① dsh 就绪行、②铸币 303 行（转发路由存在）、③客户端存活
# （代理 200 json/SSE 或 WS 隧道，`smoke_client_alive`：真 UI 启动数秒内必有，
# holder 零次——这是唯一能区分 holder 与真 UI 的机器信号）。
# 导航到达只作诊断回显，不判门；verdict 探针与 reload 赛跑（常采到 holder），
# 只取 auth 硬拦（401 真坏页），healthy/unknown/缺行一律交由存活 + 见证判定。
# $1=pid（可空：空即只查一次，进程已死不再等）。读调用方 SETTLE_WAIT 全局。
wait_settled() {
  local pid="${1:-}" i state=""
  for i in $(seq 1 "$SETTLE_WAIT"); do
    state="$(page_verdict_state)"
    # 裁决 auth 即终页确认是鉴权页，任何腿都判 FAIL（ADR page-verdict-gate 的机器可判门，verdict-honesty-repair 已并入）。
    if [[ "$state" == "auth" ]]; then
      error "终页裁决=auth（鉴权页），按失败计"
      return 1
    fi
    if log_has "$FULL_RE" && mint_seen && smoke_client_alive "${LOG:-}"; then
      log "行为落定（①+铸币303+客户端存活，用时 ${i}s）"
      return 0
    fi
    if [[ -z "$pid" ]]; then
      error "进程已退出且行为未落定（$(settle_missing)）"
      return 1
    fi
    if ! kill -0 "$pid" 2>/dev/null; then
      error "落定期进程退出且行为未落定（$(settle_missing)）"
      return 1
    fi
    sleep 1
  done
  error "落定超时（${SETTLE_WAIT}s 内未集齐①+铸币303+客户端存活：$(settle_missing)；到达 $(nav_count) 次仅诊断）"
  return 1
}

# 心跳：$1=已耗秒 $2=dsh-home。15s 一次，"慢"与"死"可区分
# （体积停涨 + 进程存活 = 慢；体积停涨 + 无进展 = 死，见 progress_watchdog_tick）。
# 读调用方 OUT/LOG 全局；缺项摘要让 CI 日志直接指出卡在哪一门。
heartbeat() {
  local elapsed="$1" home="$2" out_kb=0 log_kb=0 home_kb=0
  [[ $((elapsed % 15)) -eq 0 ]] || return 0
  out_kb=$(( $(wc -c <"${OUT:-/dev/null}" 2>/dev/null || echo 0) / 1024 ))
  if [[ -n "${LOG:-}" && -f "$LOG" ]]; then log_kb=$(( $(wc -c <"$LOG" 2>/dev/null || echo 0) / 1024 )); fi
  home_kb=$(du -sk "$home" 2>/dev/null | cut -f1 || echo 0)
  log "等待中（${elapsed}s）：OUT ${out_kb}KB host.log ${log_kb}KB dsh-home ${home_kb}KB；缺：$(settle_missing)"
}

# 无进展看门狗：OUT/host.log 的进展标记行数 + dsh-home 体积，三者连续
# _WD_STALL_SECONDS 秒零增长 → 1（停滞）；任一增长即复位计时 → 0。
# 调用方等待循环每秒调一次，循环前先调 progress_watchdog_reset。npm 下载/日志滚动
# 天然复位计时，只有真停滞才满窗——heartbeat 注释里的"慢与死可区分"在此落地为判定，
# 不再等满 SMOKE_WAIT。
_WD_STALL_SECONDS=300
progress_watchdog_reset() {
  _WD_O=-1; _WD_L=-1; _WD_H=-1; _WD_T=0
}
progress_watchdog_tick() { # $1=已耗秒 $2=dsh-home：0=活（或慢），1=停滞满窗
  local elapsed="${1:-0}" home="${2:-}" o=0 l=0 h=0 last_o=-1 last_l=-1 last_h=-1 last_t=0
  o=$(progress_markers "${OUT:-/dev/null}")
  if [[ -n "${LOG:-}" && -f "$LOG" ]]; then l=$(progress_markers "$LOG"); fi
  h=$(du -sk "$home" 2>/dev/null | cut -f1 || echo 0)
  last_o="${_WD_O:--1}"; last_l="${_WD_L:--1}"; last_h="${_WD_H:--1}"; last_t="${_WD_T:-0}"
  if [[ "$o" != "$last_o" || "$l" != "$last_l" || "$h" != "$last_h" ]]; then
    _WD_O="$o"; _WD_L="$l"; _WD_H="$h"; _WD_T="$elapsed"
    return 0
  fi
  if (( elapsed - last_t >= _WD_STALL_SECONDS )); then
    return 1
  fi
  return 0
}

# 进展标记（内容感知）行正则：只认决策与阶段信号（OUT 素行 + host.log 时间戳前缀行均命中）。
# 体积 Byte 再多也不算进展——Ryn info/Edge 代理日志等杂项字节曾把计时器反复清零
# （run 36310235841 实证），只有自家信号行才复位。注意 health/update 不在集合内：
# 页面健康 alive 与更新检查是周期性/一次性杂项，会给计时器续命（run 36316562950 实证），
# 它们的缺席与否不代表引导进展。
MARKER_RE='(\[[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9:]{8}\] )?\[(bootstrap|host|shell|nav)\]'
progress_markers() { # $1=文件：标记行数（缺失即 0）
  [[ -f "${1:-}" ]] || { echo 0; return 0; }
  grep -cE "$MARKER_RE" "$1" 2>/dev/null || true
}
# 超时回退门（R2 B1 回归锁）：未见①但见② → 0（按安装链收工）；①已见（落定失败）
# 或双无 → 1。读调用方 rc/FULL_RE/BOOT_RE 全局与 log_has。
timeout_fallback() {
  [[ ${rc:-1} -ne 0 ]] && ! log_has "$FULL_RE" && log_has "$BOOT_RE"
}

# 一等等待循环（唯一家；宿主三平台与 rpm 容器腿共用）——语义固定为 ADR
# smoke-wait-full-after-boot + shell-settle-behavior-gate：
#   ① 命中即进落定等待，落定结果即返回值（失败即 FAIL，不翻回安装链）；
#   ② 命中后不收工，继续等①至超时/退出；超时仍只有②按安装链 PASS（回退门）；
#   ③ 进程退出补扫一次（信号可能落在退出前），按最佳信号收工；
#   ④ 无进展满窗即提前收工 FAIL（零进展的②不是"慢"，是死）。
# 每次循环打心跳并跑看门狗。
# $1=stdout 路径（Linux 腿与容器腿即与 $2 同一文件） $2=host.log 路径
# $3=pid $4=dsh-home；0=落定或安装链收工，1=失败。读调用方 SMOKE_WAIT 全局。
smoke_wait_ready() {
  local out="$1" log="$2" pid="$3" home="$4" boot_seen=0 start="$SECONDS"
  # OUT/LOG 同源时经 strip_ts + sort -u 归一，无双计（见 nav_lines）。
  OUT="$out"; LOG="$log"
  progress_watchdog_reset
  for _ in $(seq 1 "$SMOKE_WAIT"); do
    if log_has "$FULL_RE"; then
      grep -m1 -E "$FULL_RE" "$OUT" 2>/dev/null || grep -m1 -E "$FULL_RE" "$LOG"
      wait_settled "$pid"
      return $?
    fi
    if [[ $boot_seen -eq 0 ]] && log_has "$BOOT_RE"; then
      boot_seen=1
      grep -m1 -E "$BOOT_RE" "$OUT" 2>/dev/null || grep -m1 -E "$BOOT_RE" "$LOG"
      log "已见②安装链（$((SECONDS - start))s），继续等①至超时/退出…"
    fi
    if ! kill -0 "$pid" 2>/dev/null; then
      if log_has "$FULL_RE"; then
        grep -m1 -E "$FULL_RE" "$OUT" 2>/dev/null || grep -m1 -E "$FULL_RE" "$LOG"
        wait_settled ""
        return $?
      fi
      if log_has "$BOOT_RE"; then log "进程已退出，未见①，按②安装链收工"; return 0; fi
      return 1
    fi
    heartbeat "$((SECONDS - start))" "$home"
    if ! progress_watchdog_tick "$((SECONDS - start))" "$home"; then
      error "冒烟停滞（${_WD_STALL_SECONDS}s 内进展标记/dsh-home 零增长、无新信号），提前收工"
      tail -30 "$log" >&2 || true
      return 1
    fi
    sleep 1
  done
  if log_has "$FULL_RE"; then
    grep -m1 -E "$FULL_RE" "$OUT" 2>/dev/null || grep -m1 -E "$FULL_RE" "$LOG"
    wait_settled "$pid"
    return $?
  fi
  if log_has "$BOOT_RE"; then
    log "${SMOKE_WAIT}s 内未见①，按②安装链收工"
    return 0
  fi
  return 1
}
