#!/usr/bin/env bash
# smoke-settle-lib.sh — 冒烟落定等待共享库（ADR smoke-settle-content-verdict）。
# linux-host/mac/win 三脚本 source 它；rpm 容器经 docker -v 挂载后 source。
# 本文件只含定义（正则常量 + 函数），source 无副作用；SMOKE_WAIT/SETTLE_WAIT/
# OUT/LOG/FULL_RE/BOOT_RE 由调用方设置，函数运行时惰性读取。
# 前提：调用方已 `set -euo pipefail`（或容器侧 `set -uo pipefail`）；函数内所有
# 可能非零的管道都显式收口（pipefail 下裸 grep 会污染调用方判断，见 nav_lines 注释）。
# shellcheck disable=SC2034  # NAV_*/SETTLE 由调用方与自测消费

NAV_RE='\[nav\] 导航已到达'
NAV_TOKEN_RE='\?token='
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

# 导航到达行（OUT 与 host.log 双写，去重防双计；任一缺失即跳过该源）。
# pipefail 注意：组内 grep 读空文件/无命中即退 1，组状态会被 pipefail 透过调用方
# 管道传出（曾致命中仍判失败）——组尾 `|| true` 把组状态恒置 0，命中与否只由外层 grep 判定。
nav_lines() {
  { [[ -n "${OUT:-}" ]] && grep -hE "$NAV_RE" "$OUT" 2>/dev/null; [[ -n "${LOG:-}" && -f "$LOG" ]] && grep -hE "$NAV_RE" "$LOG" 2>/dev/null; true; } | sort -u
}
nav_count() { nav_lines | grep -c . || true; }
# ①之后到达计数（ADR settle-gate-and-probe-retry）：Linux WebKit 只报最终提交 URL，
# token 查询串永不出现——占位到达多在①之前，唯 hop1+hop2 落在①后。以 $OUT 文件序为准；
# $OUT 无①即 0（回退 token 路径）。pipefail 下裸 grep 恒收口（nav_lines 注释同理）。
nav_count_after_ready() {
  local m=0
  if [[ -n "${OUT:-}" && -f "${OUT:-}" ]]; then
    m=$(grep -m1 -nE "$FULL_RE" "$OUT" 2>/dev/null | cut -d: -f1 || true)
    m=${m:-0}
    if [[ "$m" -gt 0 ]]; then
      tail -n +"$((m + 1))" "$OUT" 2>/dev/null | grep -cE "$NAV_RE" || true
      return 0
    fi
  fi
  echo 0
}
# 命中判定读完全部输入再退（不用 -q：-q 命中即关管道，上游 sort 收 SIGPIPE，
# pipefail 下同样误报；>/dev/null 等价静默且无此风险）。
nav_token_seen() { nav_lines | grep -E "$NAV_TOKEN_RE" >/dev/null; }

# 落定等待：①后等导航提交，再（显示腿）等应用终页裁决。$1=pid（可空：空即只查一次，进程已死不再等）。
# 到达门（≥2 到达且含 token 第二跳，或①之后到达 ≥2 次）满足后：
#   auth 裁决 → 立即 1（终页确认是鉴权页，机器可判的坏页门）；
#   其余（healthy/unknown/缺行）一律 0：外部 origin（dsh 的 http 页）上 Ryn 桥把 eval 回包
#   POST 到页面 origin（桥 `_ipcBase` 默认空串），打不到宿主 ⇒ 该页 DOM 探针恒超时，
#   与预算无关；内容见证改由截图承担（`smoke_capture_witness`）。
# 读调用方 SETTLE_WAIT 全局。
wait_settled() {
  local pid="${1:-}" i n arrived=0 state=""
  for i in $(seq 1 "$SETTLE_WAIT"); do
    n="$(nav_count)"
    if [[ "$n" -ge 2 ]] && { nav_token_seen || [[ "$(nav_count_after_ready)" -ge 2 ]]; }; then
      arrived=1
      state="$(page_verdict_state)"
      # 裁决 auth 即终页确认是鉴权页，任何腿都判 FAIL（ADR verdict-honesty-repair 的机器可判门）。
      if [[ "$state" == "auth" ]]; then
        echo "error: 落定但页面裁决=auth（终页为鉴权页），按失败计" >&2
        return 1
      fi
      echo "note: 导航已落定（到达 ${n} 次，裁决=${state:-无}，用时 ${i}s）" >&2
      return 0
    fi
    if [[ -z "$pid" ]]; then
      if [[ "$arrived" -eq 1 ]]; then
        echo "error: 进程已退出且未见页面裁决（到达 ${n} 次）" >&2
      else
        echo "error: 进程已退出且导航未落定（到达 ${n} 次，无 token 第二跳且①后不足 2 次）" >&2
      fi
      return 1
    fi
    if ! kill -0 "$pid" 2>/dev/null; then
      if [[ "$arrived" -eq 1 ]]; then
        echo "error: 落定期进程退出且未见页面裁决（到达 ${n} 次）" >&2
      else
        echo "error: 落定期进程退出且导航未落定（到达 ${n} 次）" >&2
      fi
      return 1
    fi
    sleep 1
  done
  echo "error: 落定超时（${SETTLE_WAIT}s 内未见 token 第二跳且①后到达不足 2 次；到达 $(nav_count) 次）" >&2
  return 1
}

# 心跳：$1=已耗秒 $2=dsh-home。整分触发，"慢"与"死"可区分
# （体积停涨 + 进程存活 = 慢；体积停涨 + 无进展 = 死）。读调用方 OUT/LOG 全局。
heartbeat() {
  local elapsed="$1" home="$2" out_kb=0 log_kb=0 home_kb=0
  [[ $((elapsed % 60)) -eq 0 ]] || return 0
  out_kb=$(( $(wc -c <"${OUT:-/dev/null}" 2>/dev/null || echo 0) / 1024 ))
  if [[ -n "${LOG:-}" && -f "$LOG" ]]; then log_kb=$(( $(wc -c <"$LOG" 2>/dev/null || echo 0) / 1024 )); fi
  home_kb=$(du -sk "$home" 2>/dev/null | cut -f1 || echo 0)
  echo "note: 等待中（${elapsed}s）：OUT ${out_kb}KB host.log ${log_kb}KB dsh-home ${home_kb}KB" >&2
}

# 超时回退门（R2 B1 回归锁）：未见①但见② → 0（按安装链收工）；①已见（落定失败）
# 或双无 → 1。读调用方 rc/FULL_RE/BOOT_RE 全局与 log_has。
timeout_fallback() {
  [[ ${rc:-1} -ne 0 ]] && ! log_has "$FULL_RE" && log_has "$BOOT_RE"
}
