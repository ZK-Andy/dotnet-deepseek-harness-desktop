#!/usr/bin/env bash
# smoke-crash-lib.sh — 崩溃取证共享库（ADR mac-crash-report-forensics；run 40109768884/
# 40116948207 mac x64 SIGILL 两连无证据的补课）。进程被 OS 信号杀死时 stdout 只有 shell 的
# 信号行，真因在系统崩溃报告（.ips，DiagnosticReports）。命中的报告拷进 SMOKE_LOG_DIR
# 随 artifact 上传；异常类型/终止原因/故障线程帧（映像名）打进步日志供人直读。
#
# 取证面三约束：
#   ①ReportCrash 落盘异步于进程死亡——信号死时单次快照常跑在报告写出前（40116948207：
#     SIGILL 后 1s 查询即零 .ips），须有界轮询；
#   ②Rosetta 转译崩溃可能挂异进程名，按名无果后放宽收窗内全部 .ips；
#   ③提取失败绝不连累冒烟结论（best-effort，与截图同纪律）。
#
# 旋钮（本文件头部即声明面）：
#   SMOKE_LOG_DIR — 取证输出目录（调用方/CI 注入；空即整体跳过）。
#   SMOKE_CRASH_WAIT_SECONDS — 信号死时等 .ips 落盘的轮询预算秒数（默认 15）。
#   SMOKE_CRASH_REPORT_DIRS — 诊断目录数组（数组不可 export，须同进程赋值；自测注入用。
#     未设即 macOS 真实目录：用户级 + 系统级 DiagnosticReports）。
#
# 本文件只含定义，source 无副作用；调用方须已 `set -euo pipefail` 且已 source
# lib/common.sh（log/error 消息模板）。自测夹具函数 smoke_selftest_crash 另依赖
# tpass/tfail（smoke-selftest 基座），由消费方的自测入口先行 source。
# 用法（消费方主流程）：smoke_collect_crash_reports "$APP_EXIT"（0/未观测=单次快照；
# ≥128=信号死，有界等报告落盘）。

# 收割已死应用进程的退出码（消费方主流程直接调用，不经命令替换——wait 只能等本 shell
# 的子进程）：kill -0 失败 ⇔ 进程已死（bash 对后台子进程即时收割记账、不留僵尸，
# ps stat=Z 判不死），wait 从记账即时取回退出码不阻塞；进程仍活不进入 wait。
# 结果写全局 APP_EXIT（0=活着/未观测；128+N=被信号 N 杀死）。
smoke_reap_app_exit() { # $1=pid
  APP_EXIT=0
  if kill -0 "$1" 2>/dev/null; then return 0; fi
  wait "$1" 2>/dev/null || APP_EXIT=$?
}

# 按模式收 .ips。命中即拷入 SMOKE_LOG_DIR 并打摘要；_CRASH_HITS 置 1（全局，调用方复位）。
smoke_collect_ips_matching() { # $1=find -name 模式
  local dir f
  for dir in "${SMOKE_CRASH_REPORT_DIRS[@]}"; do
    [[ -d "$dir" ]] || continue
    while IFS= read -r f; do
      _CRASH_HITS=1
      cp "$f" "$SMOKE_LOG_DIR/" 2>/dev/null || log "崩溃报告拷贝失败：$f"
      smoke_crash_report_summary "$f"
    done < <(find "$dir" -name "$1" -mmin -30 -print 2>/dev/null)
  done
}

smoke_collect_crash_reports() { # $1=应用退出码（0/未观测=单次快照；≥128=信号死，有界等报告落盘）
  [[ -n "${SMOKE_LOG_DIR:-}" ]] || return 0
  # cp 之前必须先建目录：CI 全新 workspace 下 SMOKE_LOG_DIR 不存在，缺此行则 .ips 拷贝
  # 全部失败。建目录失败（注入路径不可写）留痕后跳过取证面。
  mkdir -p "$SMOKE_LOG_DIR" 2>/dev/null || { log "崩溃取证跳过：无法创建 $SMOKE_LOG_DIR"; return 0; }
  if [[ -z "${SMOKE_CRASH_REPORT_DIRS+x}" ]]; then
    SMOKE_CRASH_REPORT_DIRS=("$HOME/Library/Logs/DiagnosticReports" "/Library/Logs/DiagnosticReports")
  fi
  # 空值先物化默认再验原始值（同消费方 SMOKE_REPAINT_SECONDS 先例）：只验替换值会让
  # set-but-empty 静默降级为零等待。非数字同样回默认——算术比较零容忍非数字。
  SMOKE_CRASH_WAIT_SECONDS="${SMOKE_CRASH_WAIT_SECONDS:-15}"
  [[ "$SMOKE_CRASH_WAIT_SECONDS" =~ ^[0-9]+$ ]] || SMOKE_CRASH_WAIT_SECONDS=15
  local app_exit="${1:-0}" wait_secs=0 start="$SECONDS"
  if (( app_exit >= 128 )); then wait_secs="$SMOKE_CRASH_WAIT_SECONDS"; fi
  # 只收本次窗口内的报告（30 分钟覆盖整个冒烟；runner 每作业全新 VM，无跨跑污染面）。
  _CRASH_HITS=0
  while :; do
    smoke_collect_ips_matching "${APP_NAME}-*.ips"
    if [[ $_CRASH_HITS -eq 1 ]]; then break; fi
    (( SECONDS - start < wait_secs )) || break
    sleep 2
  done
  if [[ $_CRASH_HITS -eq 0 ]] && (( app_exit >= 128 )); then
    smoke_collect_ips_matching '*.ips'
  fi
  if [[ $_CRASH_HITS -eq 0 ]]; then
    if (( app_exit >= 128 )); then
      log "崩溃报告：进程被信号 $((app_exit - 128)) 杀死，但 ${wait_secs}s 内诊断目录未见 .ips（ReportCrash 未落盘或该会话不产出）"
    else
      log "崩溃报告：诊断目录无本应用 .ips（未见信号死，无崩溃属预期）"
    fi
  fi
}

smoke_crash_report_summary() { # $1=.ips 路径
  python3 - "$1" <<'PY' 2>/dev/null || log "崩溃报告摘要提取失败（原文已拷入日志目录）"
import json, sys
with open(sys.argv[1], encoding="utf-8", errors="replace") as fh:
    lines = fh.read().splitlines()
body = json.loads("\n".join(lines[1:])) if len(lines) > 1 else {}
exc = body.get("exception", {})
imgs = body.get("usedImages", [])
ft = body.get("faultingThread", 0)
threads = body.get("threads", [])
frames = threads[ft].get("frames", []) if isinstance(threads, list) and ft < len(threads) else []
print(f"== 崩溃报告摘要: {sys.argv[1].rsplit('/', 1)[-1]}")
print(f"exception: {exc.get('type', '?')} signal={exc.get('signal', '?')} subtype={exc.get('subtype', '?')}")
print(f"termination: {json.dumps(body.get('termination', {}), ensure_ascii=False)}")
def image_name(frame):
    idx = frame.get("imageIndex", -1)
    return imgs[idx].get("name", "?") if isinstance(imgs, list) and 0 <= idx < len(imgs) else "?"
for frame in frames[:15]:
    print(f"  frame: {image_name(frame)} +{frame.get('imageOffset', '?')}")
PY
}

# 崩溃取证自测夹具（消费方平台夹具调用，$1=夹具根）：收割门两条（活进程返 0 /
# 死进程取回退出码）+ typo 回归钉 / 信号死轮拾 / 无报告如实措辞 / 异名兜底。
# 伪 .ips = 头行 + JSON body（与 macOS .ips 同形）。
smoke_selftest_crash() { # $1=夹具根
  local tdir="$1" bg
  local crash_in="$tdir/diag" crash_out="$tdir/crashlogs"
  local ips_body='{"exception":{"type":"EXC_BAD_INSTRUCTION","signal":"SIGILL","subtype":"unknown"},"termination":{"indicator":"SIGILL"},"faultingThread":0,"threads":[{"frames":[{"imageIndex":0,"imageOffset":4096}]}],"usedImages":[{"name":"DeepSeek.Harness.Desktop"}]}'
  mkdir -p "$crash_in" "$crash_out"
  # ⓪ 收割门（取证前置）：活进程 → APP_EXIT=0 不阻塞；死进程 → wait 从 bash 记账取回
  # 退出码（bash 对后台子进程即时收割、不留僵尸——ps stat=Z 判不死，R2 B1 实证）。
  ( sleep 30 ) & bg=$!
  smoke_reap_app_exit "$bg"
  [[ "$APP_EXIT" == 0 ]] && tpass "crash-reap-live-process-returns-zero" || tfail "crash-reap-live-process-returns-zero"
  kill "$bg" 2>/dev/null || true
  wait "$bg" 2>/dev/null || true
  ( exit 132 ) & bg=$!
  for _ in 1 2 3 4 5; do kill -0 "$bg" 2>/dev/null || break; sleep 0.2; done
  smoke_reap_app_exit "$bg"
  [[ "$APP_EXIT" == 132 ]] && tpass "crash-reap-dead-process-returns-exit-code" || tfail "crash-reap-dead-process-returns-exit-code"
  printf '%s\n%s\n' 'header' "$ips_body" >"$crash_in/${APP_NAME}-2026-10-25-010101.ips"
  SMOKE_LOG_DIR="$crash_out"
  SMOKE_CRASH_REPORT_DIRS=("$crash_in")
  # ① 非信号死 + 报告已在 → 单次即收；必须真拷进 SMOKE_LOG_DIR（40116948207 后 cp 变量名 typo 回归钉）
  smoke_collect_crash_reports 0 >"$tdir/crash1.out" 2>&1
  [[ -f "$crash_out/${APP_NAME}-2026-10-25-010101.ips" ]] && tpass "crash-copies-ips-to-logdir" || tfail "crash-copies-ips-to-logdir"
  grep -q 'EXC_BAD_INSTRUCTION' "$tdir/crash1.out" && tpass "crash-summary-parses-ips" || tfail "crash-summary-parses-ips"
  # ② 信号死 + 报告异步落盘 → 有界轮询捡到（ReportCrash 写出晚于进程死亡，40116948207 形态）
  # （夹具输入目录也要清：①的旧报告在，首轮即命中收工，异步件永远等不到）
  rm -f "$crash_in/${APP_NAME}-"*.ips "$crash_out/${APP_NAME}-"*.ips
  ( sleep 1; printf '%s\n%s\n' 'header' "$ips_body" >"$crash_in/${APP_NAME}-2026-10-25-010102.ips" ) & bg=$!
  SMOKE_CRASH_WAIT_SECONDS=2
  smoke_collect_crash_reports 132 >"$tdir/crash2.out" 2>&1
  wait "$bg" 2>/dev/null || true
  [[ -f "$crash_out/${APP_NAME}-2026-10-25-010102.ips" ]] && tpass "crash-signal-death-waits-for-late-report" || tfail "crash-signal-death-waits-for-late-report"
  # ③ 信号死 + 始终无报告 → 等满预算后如实报信号号（禁「进程非崩溃退出」误导断言）
  rm -f "$crash_in/"*.ips "$crash_out/"*.ips
  smoke_collect_crash_reports 135 >"$tdir/crash3.out" 2>&1
  grep -q '信号 7' "$tdir/crash3.out" && tpass "crash-missing-report-names-signal" || tfail "crash-missing-report-names-signal"
  # ④ 信号死 + 报告挂异进程名（Rosetta 形态）→ 按名轮询无果后放宽收窗内全部
  printf '%s\n%s\n' 'header' "$ips_body" >"$crash_in/RosettaHelper-2026-10-25-020202.ips"
  smoke_collect_crash_reports 132 >"$tdir/crash4.out" 2>&1
  [[ -f "$crash_out/RosettaHelper-2026-10-25-020202.ips" ]] && tpass "crash-fallback-collects-foreign-named-ips" || tfail "crash-fallback-collects-foreign-named-ips"
}
