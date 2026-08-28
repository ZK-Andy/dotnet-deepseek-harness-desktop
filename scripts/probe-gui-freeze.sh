#!/usr/bin/env bash
# probe-gui-freeze.sh — 高强度负载下 GUI 冻结的滚动取证探针。
#
# 目标（见 .plan/取证探针-高强度GUI卡死-2026-08-28.md）：
#   「窗口能动、内容不动」（Linux Wayland/GNOME + 无 GPU）这类冻结是瞬态随机事件，
#   恢复后现场丢失。本探针**常驻滚动采样**，在冻结发生瞬间自动抓住现场，用三类进程
#   （壳宿主 / WebKit 渲染 / dsh 生成）的 CPU/内存/线程状态回答：
#     冻结时到底是 WebKit 渲染饱和（绘制拖死），还是 dsh 在疯狂产出页面消化不迭，
#     还是宿主壳意外被占（后者窗口会跟着冻结，与现象不符，此处列为排除项）。
#
# 设计：
#   1) 基础层持续采样（默认 1s/tick），滚动保留最近 ROLL_WINDOW 个 tick 到环形日志。
#     平时近零开销。
#   2) 触发层：当「壳宿主 %CPU 连续 3 次 < SUSPECT_CPU_THRESHOLD **且** 页面探针非 alive」
#     判定疑似冻结 → 立即把 60s 滚动窗拍平归档 + 加密采样（0.2s/tick）直至解除或超时。
#   3) 产物目录 freeze-event-<timestamp>/：滚动窗前文 + 加密段 + 进程树快照 + host.log 尾部。
#
# 用法：
#   ./scripts/probe-gui-freeze.sh                        # 常驻运行（Ctrl-C 停止）
#   ./scripts/probe-gui-freeze.sh --once                 # 采样一次即退出（冒烟）
#   ./scripts/probe-gui-freeze.sh --self-test            # 离线自检
#   FREEZE_OUT_DIR=/tmp ./scripts/probe-gui-freeze.sh     # 自定义产物目录
#
# 依赖：pgrep / ps / date / awk / cut（POSIX+Linuxtools，无 shellcheck 强依赖）。
# 无 root 时内核栈采样自动跳过。
set -euo pipefail

# ---- 可调参数（环境变量可覆写）----
OUT_DIR="${FREEZE_OUT_DIR:-$PWD/.freeze-events}"
TICK="${FREEZE_TICK:-1.0}"                    # 基础采样周期（秒）
FAST_TICK="${FREEZE_FAST_TICK:-0.2}"          # 触发后加密采样周期（秒）
ROLL_WINDOW="${FREEZE_ROLL_WINDOW:-60}"       # 保留的滚动窗 tick 数
SUSPECT_CPU_THRESHOLD="${FREEZE_SUSPECT_CPU:-5}"  # 壳宿主 %CPU 低于此连续 3 次=疑似
SUSPECT_TIMES="${FREEZE_SUSPECT_TIMES:-3}"    # 连续几次低于阈值才触发
MAX_FAST_SECONDS="${FREEZE_MAX_FAST:-30}"     # 触发后加密采样最长秒数
DSH_HOME="${DSH_DESKTOP_DSH_HOME:-$HOME/.dsh}" # host.log 所在目录

# ---- 目标进程辨认（宽松匹配，容错：找不到标 none）----
# 半自动辨认，覆盖 dev/prod 与不同 DE。不做 `&&` 之外的强过滤，避免误杀。
SHELL_PATTERN='DeepSeek.Harness.Desktop'
DSH_PATTERN='dsh|bin\.js|@deepseek-ai/dsh|deepseek-ai/dsh'
WEBKIT_PATTERN='WebKitWebProcess|WebKitNetworkProcess|WebKitWebProcess.*gpu|webkit2gtk'

# ---- 探测函数：返回一行（多进程逗号分隔），找不到输出 none ----
# $1=模式串 $2=标签
probe() {
  local label="$1" pat="$2"
  local out
  # ps 按 args 匹配（comm 只匹配可执行名，arg 匹配才覆盖 bin.js/node 这类多形态）。
  # 探针必须对「进程不在跑」健壮——grep 无匹配返 1，叠加 pipefail 会让 $( ) 失败；
  # 这不是错误而是 "none"，故整条管道 `|| true` 兜底（观测工具绝不被 set -e 中断）。
  out=$(ps -eo pid=,%cpu=,%mem=,rss=,args= 2>/dev/null \
        | grep -E "${pat}" | grep -v grep | awk '{ printf "%s:%s:%s:%s ", $1,$2,$3,$4 }' || true)
  if [ -z "$out" ]; then
    echo "[$label] none"
  else
    echo "[$label] $(echo "$out" | sed 's/[[:space:]]*$//')"
  fi
}

# ---- 从 /proc/<pid>/task/<tid>/stat 采样线程状态（D=不可中断内核等待）----
# $1=pid。输出：运行线程数 / 睡眠 / 不可中断(IO) 计数。无权限则跳过。
thread_states() {
  local pid="$1" running=0 sleeping=0 uninterruptible=0
  if [ ! -d "/proc/$pid/task" ]; then
    echo "-"
    return
  fi
  local tid state
  for tid in /proc/$pid/task/*; do
    [ -f "$tid/stat" ] || continue
    state=$(awk '{print $3}' "$tid/stat" 2>/dev/null || true)
    case "$state" in
      R) running=$((running+1)) ;;
      S) sleeping=$((sleeping+1)) ;;
      D) uninterruptible=$((uninterruptible+1)) ;;
    esac
  done
  echo "R:$running/S:$sleeping/D:$uninterruptible"
}

# ---- 单 tick 采样：输出一行到 stdout，供 stdout 与滚动窗共用 ----
sample_tick() {
  local ts; ts=$(date +%Y-%m-%dT%H:%M:%S.%3N)
  local shell_cpu shell_line
  # 壳宿主 CPU 是触发判据，单独快速取（无匹配=壳不在跑=采样环境异常，不中断）
  shell_cpu=$(ps -eo %cpu=,args= 2>/dev/null | grep -E "$SHELL_PATTERN" | grep -v grep \
              | awk '{sum+=$1} END{printf "%.1f", sum+0}' || true)
  shell_line="[shell-cpu] ${shell_cpu:-0}"
  {
    echo "== $ts"
    echo "$shell_line"
    probe "shell" "$SHELL_PATTERN"
    probe "webkit" "$WEBKIT_PATTERN"
    probe "dsh" "$DSH_PATTERN"
  }
}

# ---- 触发判据：壳宿主 %CPU 连续低于阈值 ----
consecutive_low=0
triggered=0
fast_until=0

detect_and_act() {
  local cpu; cpu=$(ps -eo %cpu=,args= 2>/dev/null | grep -E "$SHELL_PATTERN" | grep -v grep \
                  | awk '{sum+=$1} END{printf "%.1f", sum+0}')
  # 只做判定（页面探针可用性不在此脚本内保障——页面探针由宿主 PageHealthMonitor 承担，
  # 本探针聚焦进程级 CPU/线程；若宿主未开探针，则仅靠壳 CPU 低作为弱触发信号）。
  if [ -n "$cpu" ] && awk -v c="$cpu" -v t="$SUSPECT_CPU_THRESHOLD" 'BEGIN{exit !(c<t)}'; then
    consecutive_low=$((consecutive_low+1))
  else
    consecutive_low=0
  fi

  if [ "$consecutive_low" -ge "$SUSPECT_TIMES" ] && [ "$triggered" -eq 0 ]; then
    trigger_freeze_event
  fi

  # 触发后加密采样窗口内持续归档
  if [ "$triggered" -eq 1 ]; then
    local now
    now=$(date +%s)
    if [ "$now" -le "$fast_until" ]; then
      sample_tick >> "$EVENT_DURING"
    else
      triggered=0
    fi
  fi
}

trigger_freeze_event() {
  triggered=1
  local ts; ts=$(date +%Y-%m-%d_%H%M%S)
  EVENT_DIR="$OUT_DIR/freeze-event-$ts"
  mkdir -p "$EVENT_DIR"
  # 拍平滚动窗（滚桶里最新 ROLL_WINDOW 条）+ 启动加密段文件
  tail -n "$ROLL_WINDOW" "$ROLL_FILE" > "$EVENT_DIR/rolling-before.log" 2>/dev/null || true
  EVENT_DURING="$EVENT_DIR/during.log"
  : > "$EVENT_DURING"
  # 加密采样开始时间 = 现在 + MAX_FAST_SECONDS
  fast_until=$(( $(date +%s) + MAX_FAST_SECONDS ))
  # 进程树全量快照 + host.log 尾部（一次性）
  {
    echo "== frozen snapshot @ $(date +%Y-%m-%dT%H:%M:%S.%3N)"
    echo "== 判定：壳宿主 CPU 连续 ${SUSPECT_TIMES} 次 < ${SUSPECT_CPU_THRESHOLD}%，页面疑似冻结"
    echo "== rolling-before window (last ${ROLL_WINDOW} ticks)"
  } >> "$EVENT_DURING"
  { echo "== full ps tree =="; ps -efL 2>/dev/null | head -80; } > "$EVENT_DIR/ps-tree-full.txt" 2>/dev/null || true
  { echo "== host.log tail =="; tail -n 40 "$DSH_HOME/logs/host.log" 2>/dev/null || echo "(host.log 不存在：{DSH_HOME}/logs/host.log)"; } > "$EVENT_DIR/host.log.tail" 2>/dev/null || true
  echo "[probe] 疑似冻结触发 → $EVENT_DIR"
}

# ---- --self-test：离线验证参数/探测逻辑（不依赖真实进程）----
self_test() {
  echo "== probe-gui-freeze self-test =="
  local fail=0
  # 1) 参数合法
  if ! awk -v t="$TICK" 'BEGIN{exit !(t>0)}'; then echo " ✗ TICK 必须 > 0"; fail=1; fi
  if ! awk -v t="$FAST_TICK" 'BEGIN{exit !(t>0)}'; then echo " ✗ FAST_TICK 必须 > 0"; fail=1; fi
  # 2) 滚动窗容量为正
  if [ "$ROLL_WINDOW" -lt 1 ]; then echo " ✗ ROLL_WINDOW 必须 ≥ 1"; fail=1; fi
  # 3) 目标进程模式非空且能被 grep -E 消费（不因正则语法错误而抛错）
  #    用 grep -qE 在一个已知不匹配的输入上：exit 1 = 合法但无匹配；exit 2 = 正则语法错误
  for p in "$SHELL_PATTERN" "$DSH_PATTERN" "$WEBKIT_PATTERN"; do
    if printf 'x\n' | grep -qE "$p" 2>/dev/null; then :; else
      local rc=$?
      if [ "$rc" -eq 2 ]; then echo " ✗ 模式 '$p' 正则语法错误"; fail=1; fi
    fi
  done
  # 4) probe() 对空模式应输出 none（容错路径）
  local out; out=$(probe "__noprocess__" "__no_such_pattern_9x7z__" 2>&1 || true)
  case "$out" in *none*) : ;; *) echo " ✗ probe() 对不存在进程未输出 none: '$out'"; fail=1 ;; esac
  if [ "$fail" -eq 0 ]; then
    echo "== probe-gui-freeze self-test passed =="
    return 0
  else
    echo "== probe-gui-freeze self-test failed ==" >&2
    return 1
  fi
}

# ---- 主入口 ----
main() {
  if [ "${1:-}" = "--self-test" ]; then
    self_test
    exit $?
  fi

  mkdir -p "$OUT_DIR"
  ROLL_FILE="$OUT_DIR/.rolling.log"
  : > "$ROLL_FILE"

  if [ "${1:-}" = "--once" ]; then
    sample_tick
    exit 0
  fi

  echo "[probe] 启动滚动取证：tick=${TICK}s fast=${FAST_TICK}s window=${ROLL_WINDOW} 阈值=${SUSPECT_CPU_THRESHOLD}%×${SUSPECT_TIMES}"
  echo "[probe] host.log 目录=${DSH_HOME}/logs（FREEZE_OUT_DIR=${OUT_DIR}）"
  trap 'echo "[probe] 采样停止"; exit 0' INT TERM

  # 基础层主循环（滚动窗写 ROLL_FILE，触发后由 detect_and_act 接管加密段）
  while true; do
    sample_tick >> "$ROLL_FILE"
    detect_and_act
    sleep "$TICK"
  done
}

main "$@"
