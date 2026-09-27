#!/usr/bin/env bash
# smoke-settle-lib.sh — 冒烟落定等待共享库（ADR shell-settle-behavior-gate）。
# linux-host/mac/win 三脚本 source 它；rpm 容器经 docker -v 挂载后 source。
# 本文件只含定义（正则常量 + 函数），source 无副作用；SMOKE_WAIT/SETTLE_WAIT/
# OUT/LOG/FULL_RE/BOOT_RE 由调用方设置，函数运行时惰性读取。
# 前提：调用方已 `set -euo pipefail`（或容器侧 `set -uo pipefail`）；函数内所有
# 可能非零的管道都显式收口（pipefail 下裸 grep 会污染调用方判断，见 nav_lines 注释）。
# shellcheck disable=SC2034  # NAV_*/SETTLE 由调用方与自测消费

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
  grep -qE "$1" "$OUT" 2>/dev/null || { [[ -n "${LOG:-}" && -f "$LOG" ]] && grep -qE "$1" "$LOG"; }
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
    # 裁决 auth 即终页确认是鉴权页，任何腿都判 FAIL（ADR verdict-honesty-repair 的机器可判门）。
    if [[ "$state" == "auth" ]]; then
      echo "error: 终页裁决=auth（鉴权页），按失败计" >&2
      return 1
    fi
    if log_has "$FULL_RE" && mint_seen && smoke_client_alive "${LOG:-}"; then
      echo "note: 行为落定（①+铸币303+客户端存活，用时 ${i}s）" >&2
      return 0
    fi
    if [[ -z "$pid" ]]; then
      echo "error: 进程已退出且行为未落定（$(settle_missing)）" >&2
      return 1
    fi
    if ! kill -0 "$pid" 2>/dev/null; then
      echo "error: 落定期进程退出且行为未落定（$(settle_missing)）" >&2
      return 1
    fi
    sleep 1
  done
  echo "error: 落定超时（${SETTLE_WAIT}s 内未集齐①+铸币303+客户端存活：$(settle_missing)；到达 $(nav_count) 次仅诊断）" >&2
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
  echo "note: 等待中（${elapsed}s）：OUT ${out_kb}KB host.log ${log_kb}KB dsh-home ${home_kb}KB；缺：$(settle_missing)" >&2
}

# 无进展看门狗：OUT/host.log/dsh-home 三体积连续 _WD_STALL_SECONDS 秒零增长
# → 1（停滞）；任一增长即复位计时 → 0。调用方等待循环每秒调一次，循环前先调
# progress_watchdog_reset。npm 下载/日志滚动天然复位计时，只有真停滞才满窗——
# heartbeat 注释里的"慢与死可区分"在此落地为判定，不再等满 SMOKE_WAIT。
_WD_STALL_SECONDS=300
progress_watchdog_reset() {
  _WD_O=-1; _WD_L=-1; _WD_H=-1; _WD_T=0
}
progress_watchdog_tick() { # $1=已耗秒 $2=dsh-home：0=活（或慢），1=停滞满窗
  local elapsed="${1:-0}" home="${2:-}" o=0 l=0 h=0 last_o=-1 last_l=-1 last_h=-1 last_t=0
  o=$(wc -c <"${OUT:-/dev/null}" 2>/dev/null || echo 0)
  if [[ -n "${LOG:-}" && -f "$LOG" ]]; then l=$(wc -c <"$LOG" 2>/dev/null || echo 0); fi
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

# 超时回退门（R2 B1 回归锁）：未见①但见② → 0（按安装链收工）；①已见（落定失败）
# 或双无 → 1。读调用方 rc/FULL_RE/BOOT_RE 全局与 log_has。
timeout_fallback() {
  [[ ${rc:-1} -ne 0 ]] && ! log_has "$FULL_RE" && log_has "$BOOT_RE"
}

# 截图内容见证（ADR page-verdict-gate）：外部 origin 上 DOM 探针回不来，故内容真伪由**截图本身**判——
# 近空白（401 墙：实测 mean≈1.00/sd≈0.04）与深色引导页（mean≈0.14）判失败，真 UI（mean≈0.81/sd≈0.13）通过。
# 阈值取自 CI 实测四图；无 convert 即 fail loud（调用点已限定显示腿）。
# $1=截图路径；$2=裁剪几何（默认 1200x800+0+0，Linux Xvfb 全屏沿用）；
# $3=gravity（默认空；mac 全屏截图含菜单栏/Dock 时传 center 取中央避边框 chrome）。0=内容像 UI。
# bash 3.2 安全：无数组展开（mac runner 默认 bash 3.2，空数组 + set -u 即炸）。
smoke_capture_witness() {
  local shot="$1" crop="${2:-1200x800+0+0}" gravity="${3:-}" stats mean sd
  [[ -s "$shot" ]] || { echo "error: 截图缺失，内容见证不通过：$shot" >&2; return 1; }
  command -v convert >/dev/null 2>&1 || { echo "error: 无 convert，截图内容见证无法执行（显示腿须装 imagemagick）" >&2; return 1; }
  if [[ -n "$gravity" ]]; then
    stats="$(convert "$shot" -gravity "$gravity" -crop "$crop" +repage -colorspace Gray -format '%[fx:mean] %[fx:standard_deviation]' info: 2>/dev/null || true)"
  else
    stats="$(convert "$shot" -crop "$crop" +repage -colorspace Gray -format '%[fx:mean] %[fx:standard_deviation]' info: 2>/dev/null || true)"
  fi
  mean="${stats%% *}"; sd="${stats##* }"
  if [[ -z "$mean" || -z "$sd" || "$mean" == "$stats" ]]; then
    echo "error: 截图统计失败（ImageMagick），内容见证不通过" >&2; return 1
  fi
  if awk "BEGIN{exit !($mean >= 0.35 && $sd >= 0.08)}"; then
    echo "note: 截图内容见证通过（mean=${mean} sd=${sd}）" >&2; return 0
  fi
  echo "error: 截图内容见证不通过（mean=${mean} sd=${sd}）：近空白/深色页（401 墙或引导页）不算 UI" >&2
  return 1
}

# 客户端存活门（ADR loopback-forward-proxy）：浅色主题真 UI 的 mean/sd 与空白页重叠
# （mac light UI mean≈0.99/sd≈0.04 vs 401 墙 sd≈0.04），像素无法区分；改证行为——
# dsh 客户端启动后必经代理发 RPC/SSE/WS（同源），host.log 里 200 回包/流转或 WS 隧道
# 合计 ≥3 即活（实测启动数秒内 ~10 次）。三种前缀都要数：缓冲体走 `代理回包：`、
# 流式走 `代理流转：`（只数其一即漏数，R3 实证）、WS 走 `代理升级隧道已建`
# （remote.mux 无 200 行，纯 WS 形态会漏数）。holder 零次——落定门的行为支即此。
# $1=host.log 路径。0=存活（落定门行为支 + 像素见证 OR 组绿）。
smoke_client_alive() {
  local log="${1:-}" n=0
  [[ -f "$log" ]] || return 1
  n=$(grep -cE "代理(流转|回包)：200 (application/json|text/event-stream)|代理升级隧道已建" "$log" 2>/dev/null || true)
  [[ "${n:-0}" -ge 3 ]]
}
