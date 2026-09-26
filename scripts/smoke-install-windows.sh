#!/usr/bin/env bash
# smoke-install-windows.sh — Windows 安装冒烟（ADR artifact-verification-chain 平台补齐）。
# 对 Inno Setup 安装器做「静默安装 → 启动 → 等判定信号」验证，补齐 win 平台此前
# 只有 staging 布局断言、无「装得上、起得来」覆盖的缺口（libadwaita 事件同类的
# 缺依赖/起不来事故在 win 的对应面：WebView2 runtime、VC++ 运行库、原生 DLL）。
#
# 判定信号与 Linux 冒烟同款双信号：
#   ①`[host] dsh web =` = dsh 就绪（传输层）；
#   ②`[bootstrap] 引导开始：` = 安装链保底。
# 等待语义（ADR smoke-wait-full-after-boot）：②命中后不收工，继续等①至
# 超时或进程退出；超时仍只有②按安装链 PASS，进程退出按退出时最佳信号收工。
# 落定语义（ADR smoke-settle-content-verdict）：①只是 dsh 就绪行，verdict 与截图
# 必须等导航落定——`[nav] 导航已到达` 去重 ≥2 次且含 `?token=` 第二跳。落定超时或
# 落定期进程退出即 FAIL（dsh 已就绪但 UI 未落定是真实事故，不再按 full-chain 放行）。
# 实测边界（2026-08-29 首跑）：Windows runner 的壳同样在窗口创建（Ryn Run）即
# 退出——WebView2 初始化的原生依赖在 runner 环境不可用，全链信号不可达，冒烟
# 停在②安装链位；「装得上、起得来」的启动段覆盖由此完成。
# 运行态实验（ADR smoke-runner-deepening）：package-windows.yml 在冒烟前装
# WebView2 Evergreen，若此后 ① 命中则运行态自动进 CI——verdict 行即结论。
#
# 信号源 = <DSH_HOME>/logs/host.log（HostLog 双写 stdout 与该文件）+ 启动器 stdout
# 捕获（工程为 Exe 控制台子系统，重定向通常可达；host.log 为权威源），双源并查，
# 去重防双计。
#
# 等待窗与引导步超时强耦合的是单轮尝试预算（RuntimeBootstrapOptions.StepTimeoutMinutes
# 默认 10 分钟单步上限 + 120s 余量 = 720s；重试轮不计入——冒烟只等首轮落定）。
# Windows runner 多有预装 node（复用免下载），全链耗时可观仍在窗内。
# SMOKE_WAIT_SECONDS / SMOKE_SETTLE_SECONDS 可覆写。
#
# 用法: smoke-install-windows.sh <setup.exe>
# 自测: smoke-install-windows.sh --self-test（纯函数回归，不碰安装器）
set -euo pipefail

SELFTEST=0
if [[ "${1:-}" == "--self-test" ]]; then SELFTEST=1; fi
if [[ "$SELFTEST" -eq 0 ]]; then
  SETUP="${1:?usage: smoke-install-windows.sh <setup.exe>}"
  [[ -f "$SETUP" ]] || { echo "error: 安装器不存在: $SETUP" >&2; exit 1; }
  SETUP="$(realpath "$SETUP")"
fi
APP_NAME="DeepSeek.Harness.Desktop"

# Git Bash 会把 /VERYSILENT 这类开关当 POSIX 路径转换（cookbook [脚本]；robocopy
# 同款先例），MSYS2_ARG_CONV_EXCL 排除。
export MSYS2_ARG_CONV_EXCL='*'

SMOKE_WAIT="${SMOKE_WAIT_SECONDS:-720}"
# 落定窗：①出现后等导航提交（commit 延迟毫秒级，90s 只防 runner 卡顿）。
SETTLE_WAIT="${SMOKE_SETTLE_SECONDS:-90}"
FULL_RE='\[host\] dsh web ='
BOOT_RE='\[bootstrap\] 引导开始：'
PASS_RE="$FULL_RE|$BOOT_RE"

# 落定等待共享库（NAV 正则 + log_has/nav/wait_settled/heartbeat/timeout_fallback）。
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "$SCRIPT_DIR/smoke-settle-lib.sh"

# 判定结论（ADR smoke-runner-deepening）：命中 ① 全链还是 ② 安装链必须打印成结论。
smoke_verdict() { # $1=stdout $2=host.log
  if grep -qE '\[host\] dsh web =' "$1" 2>/dev/null || { [[ -f "$2" ]] && grep -qE '\[host\] dsh web =' "$2"; }; then
    echo "SMOKE_VERDICT=full-chain（dsh web 就绪）"
  else
    echo "SMOKE_VERDICT=install-chain（仅引导启动）"
  fi
}

# 启动截图 best-effort（ADR smoke-runner-deepening）：供人眼复核，永不拦冒烟。
# 落盘目录由调用方经 SMOKE_SHOT_DIR 注入；未设（本地跑）即跳过。无桌面会话时静默跳过。
smoke_shot() { # $1=文件名
  [[ -n "${SMOKE_SHOT_DIR:-}" ]] || return 0
  mkdir -p "$SMOKE_SHOT_DIR" 2>/dev/null || return 0
  local winshot
  winshot="$(cygpath -w "$SMOKE_SHOT_DIR/$1" 2>/dev/null || echo "$SMOKE_SHOT_DIR/$1")"
  SMOKE_SHOT_WIN="$winshot" powershell -NoProfile -Command \
    "Add-Type -AssemblyName System.Drawing,System.Windows.Forms; \$s=[Windows.Forms.Screen]::PrimaryScreen.Bounds; \$b=New-Object Drawing.Bitmap(\$s.Width,\$s.Height); \$g=[Drawing.Graphics]::FromImage(\$b); \$g.CopyFromScreen(0,0,0,0,\$b.Size); \$b.Save(\$env:SMOKE_SHOT_WIN); \$g.Dispose(); \$b.Dispose()" 2>/dev/null \
    || echo "note: 截图跳过（无桌面会话）" >&2
}

# 落定等待与心跳实现在 smoke-settle-lib.sh（上已 source）。

smoke_self_test() { # 纯函数回归：夹具断言 verdict/落定/心跳/回退门（函数实现在 smoke-settle-lib.sh）
  local tdir fail=0 live
  tdir="$(mktemp -d)"
  OUT="$tdir/out"; LOG="$tdir/host.log"; HOME_DIR="$tdir/home"; mkdir -p "$HOME_DIR"
  tpass() { echo "ok: $1"; }
  tfail() { echo "FAIL: $1"; fail=1; }
  echo "[host] dsh web = http://127.0.0.1:1/?token=t" >"$OUT"; : >"$LOG"
  [[ "$(smoke_verdict "$OUT" "$LOG")" == *"full-chain"* ]] && tpass "verdict-full" || tfail "verdict-full"
  : >"$OUT"; : >"$LOG"
  [[ "$(smoke_verdict "$OUT" "$LOG")" == *"install-chain"* ]] && tpass "verdict-install" || tfail "verdict-install"
  printf '[nav] 导航已到达：http://127.0.0.1:1/（origin → x）\n[nav] 导航已到达：http://127.0.0.1:1/?token=t → y\n' >"$OUT"
  sleep 30 & live=$!
  SETTLE_WAIT=90 wait_settled "$live" >/dev/null 2>&1 && tpass "settle-ok" || tfail "settle-ok"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  printf '[nav] 导航已到达：http://127.0.0.1:1/（origin → x）\n' >"$OUT"; : >"$LOG"
  sleep 30 & live=$!
  SETTLE_WAIT=2 wait_settled "$live" >/dev/null 2>&1 && tfail "settle-timeout-should-fail" || tpass "settle-timeout-fails"
  kill "$live" 2>/dev/null || true; wait "$live" 2>/dev/null || true
  SETTLE_WAIT=90 wait_settled "" >/dev/null 2>&1 && tfail "settle-deadpid-should-fail" || tpass "settle-deadpid-fails"
  heartbeat "60" "$HOME_DIR" 2>&1 | grep -q "等待中（60s）" && tpass "heartbeat" || tfail "heartbeat"
  # 回退门（R2 B1 回归锁）：①已见不得翻回；纯②才翻回；双无不翻
  rc=1
  printf '[bootstrap] 引导开始：x\n[host] dsh web = http://127.0.0.1:1/?token=t\n' >"$OUT"
  timeout_fallback >/dev/null 2>&1 && tfail "fallback-full-should-not-flip" || tpass "fallback-full-noflip"
  printf '[bootstrap] 引导开始：x\n' >"$OUT"
  timeout_fallback >/dev/null 2>&1 && tpass "fallback-boot-flips" || tfail "fallback-boot-flips"
  : >"$OUT"; : >"$LOG"
  timeout_fallback >/dev/null 2>&1 && tfail "fallback-empty-should-not-flip" || tpass "fallback-empty-noflip"
  rm -rf "$tdir"
  [[ $fail -eq 0 ]] && echo "self-test: PASS" || echo "self-test: FAIL"
  return $fail
}

if [[ "$SELFTEST" -eq 1 ]]; then
  smoke_self_test
  exit $?
fi

INSTALL_DIR="$(mktemp -d)/app"
HOME_DIR="$(mktemp -d)"
OUT="$(mktemp)"
WIN_DIR="$(cygpath -w "$INSTALL_DIR" 2>/dev/null || echo "$INSTALL_DIR")"
APP_EXE="$INSTALL_DIR/$APP_NAME.exe"

cleanup() {
  powershell -NoProfile -Command "Stop-Process -Name '$APP_NAME' -Force -ErrorAction SilentlyContinue" >/dev/null 2>&1 || true
  rm -rf "$OUT"
}
trap cleanup EXIT

echo "== 安装（静默，DIR=$WIN_DIR）"
# 安装环节取证：Inno 是 GUI 子系统，stdout 恒空——唯一诊断面是 /LOG 安装日志
# （逐文件动作）。安装器后台运行 + 步级超时（首次实跑 88MB 闭包静默装曾 >7min
# 无任何输出，用户终止——卡在哪一步只能靠 /LOG 回答）；超时即 dump 日志尾部 +
# 进程表 fail loud，绝不静默挂死。
WIN_LOG="$(cygpath -w "$HOME_DIR/install.log" 2>/dev/null || echo "$HOME_DIR/install.log")"
INSTALL_WAIT="${INSTALL_WAIT_SECONDS:-300}"
# 预建日志：排除「路径不可写/未创建」变量——Inno 正常初始化必然立即写日志，
# 超时后日志仍空 = 安装器从未进入 Inno 初始化（执行前阻塞，如对话框/扫描）
: > "$HOME_DIR/install.log"
# stdin 断开（</dev/null）：CI 中 GUI 安装器继承 bash 管道句柄后启动期挂死是
# 已知坑形态
"$SETUP" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="$WIN_LOG" /DIR="$WIN_DIR" </dev/null &
setup_pid=$!
install_done=0
for _ in $(seq 1 "$INSTALL_WAIT"); do
  if ! kill -0 "$setup_pid" 2>/dev/null; then install_done=1; break; fi
  sleep 1
done
if [[ $install_done -eq 0 ]]; then
  echo "error: [win] 安装器 ${INSTALL_WAIT}s 未退出（疑似卡住）。install.log 尾部：" >&2
  tail -40 "$HOME_DIR/install.log" >&2 || true
  echo "--- 进程表（setup/DeepSeek 相关）---" >&2
  tasklist 2>/dev/null | grep -iE "setup|deepseek" >&2 || true
  echo "--- 窗口定性（Responding/MainWindowTitle：有对话框直接现形）---" >&2
  powershell -NoProfile -Command "Get-Process | Where-Object { \$_.ProcessName -match 'DeepSeek|setup' } | Select-Object Id,ProcessName,Responding,MainWindowTitle | Format-Table -AutoSize | Out-String -Width 200" >&2 || true
  powershell -NoProfile -Command "Get-Process | Where-Object { \$_.MainWindowTitle -ne '' } | Select-Object ProcessName,MainWindowTitle | Format-Table -AutoSize | Out-String -Width 200" >&2 || true
  kill -9 "$setup_pid" 2>/dev/null || true
  exit 1
fi
set +e
wait "$setup_pid"
install_rc=$?
set -e
if [[ $install_rc -ne 0 || ! -f "$APP_EXE" ]]; then
  echo "error: [win] 安装器退出码 $install_rc 或缺主程序。install.log 尾部：" >&2
  tail -40 "$HOME_DIR/install.log" >&2 || true
  exit 1
fi
if [[ ! -f "$INSTALL_DIR/unins000.exe" ]]; then
  # 真安装器语义断言：无卸载器 = 产物是自解压包而非安装器（历史静默降级事故的判别位）
  echo "error: 安装后缺 unins000.exe——产物疑似非 Inno 安装器（回退链静默降级？）" >&2
  exit 1
fi
echo "== 安装完成（install.log 尾部留痕）"
tail -3 "$HOME_DIR/install.log" >&2
echo "== 启动冒烟（等①就绪后等导航落定，②保底；窗=${SMOKE_WAIT}s/落定${SETTLE_WAIT}s）"
set +e
# 无人值守跳过可选插件（ADR preinstall-unattended-skip）：CI 无人点选，省 5 分钟决策等待。
env DSH_DESKTOP_DSH_HOME="$HOME_DIR" DEEPSEEK_API_KEY=placeholder DSH_DESKTOP_PREINSTALL_AUTO=skip \
  "$APP_EXE" >"$OUT" 2>&1 &
pid=$!
rc=1
boot_seen=0
SECONDS=0
LOG="$HOME_DIR/logs/host.log"
log_has() { # $1=正则：stdout 或 host.log 任一命中
  grep -qE "$1" "$OUT" 2>/dev/null || { [[ -f "$LOG" ]] && grep -qE "$1" "$LOG"; }
}
for _ in $(seq 1 "$SMOKE_WAIT"); do
  if log_has "$FULL_RE"; then
    grep -m1 -E "$FULL_RE" "$OUT" 2>/dev/null || grep -m1 -E "$FULL_RE" "$LOG"
    if wait_settled "$pid"; then
      rc=0
      smoke_verdict "$OUT" "$LOG"
      smoke_shot "smoke-windows.png"
      # PASS 也打印壳输出尾部：壳何时/为何退出（如窗口创建即退出）需要证据在案
      echo "--- 壳输出尾部（PASS 证据）---" >&2
      tail -5 "$OUT" >&2 || true
    else
      rc=1
      smoke_shot "smoke-windows-fail.png"
      echo "--- 壳输出尾部（FAIL 证据）---" >&2
      tail -30 "$OUT" >&2 || true
      if [[ -f "$LOG" ]]; then
        echo "--- host.log 尾部 ---" >&2
        tail -30 "$LOG" >&2 || true
      fi
    fi
    break
  fi
  if [[ $boot_seen -eq 0 ]] && log_has "$BOOT_RE"; then
    boot_seen=1
    grep -m1 -E "$BOOT_RE" "$OUT" 2>/dev/null || grep -m1 -E "$BOOT_RE" "$LOG"
    echo "note: 已见②安装链（${SECONDS}s），继续等①至超时/退出…" >&2
  fi
  if ! kill -0 "$pid" 2>/dev/null; then
    # 进程已退出：补扫一次（信号可能刚好落在退出前），按最佳信号收工
    if log_has "$FULL_RE"; then
      grep -m1 -E "$FULL_RE" "$OUT" 2>/dev/null || grep -m1 -E "$FULL_RE" "$LOG"
      if wait_settled ""; then
        rc=0
        smoke_verdict "$OUT" "$LOG"
        smoke_shot "smoke-windows.png"
        echo "--- 壳输出尾部（PASS 证据）---" >&2
        tail -5 "$OUT" >&2 || true
      else
        rc=1
        smoke_shot "smoke-windows-fail.png"
        echo "--- 壳输出尾部（FAIL 证据）---" >&2
        tail -30 "$OUT" >&2 || true
      fi
    elif log_has "$BOOT_RE"; then
      echo "note: 进程已退出，未见①，按②安装链收工" >&2
      rc=0
      smoke_verdict "$OUT" "$LOG"
      smoke_shot "smoke-windows.png"
      echo "--- 壳输出尾部（PASS 证据）---" >&2
      tail -5 "$OUT" >&2 || true
    fi
    break
  fi
  heartbeat "$SECONDS" "$HOME_DIR"
  sleep 1
done
# 超时仍只有②：按安装链 PASS（等满窗语义），而非失败。回退门保证①已见时不翻回
# （R2 B1：①已见 + 落定失败必须保持 FAIL）。
if timeout_fallback; then
  echo "note: ${SMOKE_WAIT}s 内未见①，按②安装链收工" >&2
  rc=0
  smoke_verdict "$OUT" "$LOG"
  smoke_shot "smoke-windows.png"
  echo "--- 壳输出尾部（PASS 证据）---" >&2
  tail -5 "$OUT" >&2 || true
fi
set -e
if [[ $rc -ne 0 ]]; then
  echo "error: [win] 冒烟失败——${SMOKE_WAIT}s 内未出现 dsh web URL 或引导启动行。stdout 尾部：" >&2
  tail -30 "$OUT" >&2 || true
  if [[ -f "$LOG" ]]; then
    echo "--- host.log 尾部 ---" >&2
    tail -30 "$LOG" >&2
  fi
  smoke_shot "smoke-windows-fail.png"
fi
kill "$pid" 2>/dev/null || true
wait "$pid" 2>/dev/null || true
exit $rc
