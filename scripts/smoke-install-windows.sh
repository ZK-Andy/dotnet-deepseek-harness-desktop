#!/usr/bin/env bash
# smoke-install-windows.sh — Windows 安装冒烟（ADR artifact-verification-chain 平台补齐）。
# 对 Inno Setup 安装器做「静默安装 → 启动 → 等判定信号」验证，补齐 win 平台此前
# 只有 staging 布局断言、无「装得上、起得来」覆盖的缺口（libadwaita 事件同类的
# 缺依赖/起不来事故在 win 的对应面：WebView2 runtime、VC++ 运行库、原生 DLL）。
#
# 判定信号与 Linux 冒烟同款双信号：
#   ①`[host] dsh web =` = dsh 就绪（传输层）；
#   ②`[bootstrap] 引导开始：` = 安装链保底。
# 等待/落定/心跳/看门狗/回退门语义在 scripts/lib/smoke-wait-lib.sh（唯一家，三平台同一实现）；
# 裁决门（auth 硬拦）、存活门、证据打印在 scripts/lib/smoke-verdict-lib.sh。
# 实测边界（2026-08-29 首跑）：Windows runner 的壳同样在窗口创建（Ryn Run）即
# 退出——WebView2 初始化的原生依赖在 runner 环境不可用，全链信号不可达，冒烟
# 停在②安装链位；「装得上、起得来」的启动段覆盖由此完成。
# 运行态实验（ADR smoke-runner-deepening）：package.yml 的 windows 腿在冒烟前装
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

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/smoke-verdict-lib.sh
source "$SCRIPT_DIR/lib/smoke-verdict-lib.sh"
# shellcheck source=lib/smoke-selftest-verdict.sh
source "$SCRIPT_DIR/lib/smoke-selftest-verdict.sh"

SELFTEST=0
if [[ "${1:-}" == "--self-test" ]]; then SELFTEST=1; fi
if [[ "$SELFTEST" -eq 0 ]]; then
  SETUP="${1:?usage: smoke-install-windows.sh <setup.exe>}"
  [[ -f "$SETUP" ]] || die "安装器不存在: $SETUP"
  SETUP="$(realpath "$SETUP")"
fi
APP_NAME="DeepSeek.Harness.Desktop"

# Git Bash 会把 /VERYSILENT 这类开关当 POSIX 路径转换（cookbook [脚本]；robocopy
# 同款先例），MSYS2_ARG_CONV_EXCL 排除。
export MSYS2_ARG_CONV_EXCL='*'

# 等待窗/落定窗（含覆写旋钮）与判定串 ①②的唯一家在共享库，此处只做一次解析。
smoke_resolve_windows

# 启动截图 best-effort（ADR smoke-runner-deepening）：供人眼复核，永不拦冒烟。
# 落盘目录由调用方经 SMOKE_SHOT_DIR 注入；未设（本地跑）即跳过。无桌面会话时静默跳过。
smoke_shot() { # $1=文件名
  [[ -n "${SMOKE_SHOT_DIR:-}" ]] || return 0
  mkdir -p "$SMOKE_SHOT_DIR" 2>/dev/null || return 0
  local winshot
  winshot="$(cygpath -w "$SMOKE_SHOT_DIR/$1" 2>/dev/null || echo "$SMOKE_SHOT_DIR/$1")"
  SMOKE_SHOT_WIN="$winshot" powershell -NoProfile -Command \
    "Add-Type -AssemblyName System.Drawing,System.Windows.Forms; \$s=[Windows.Forms.Screen]::PrimaryScreen.Bounds; \$b=New-Object Drawing.Bitmap(\$s.Width,\$s.Height); \$g=[Drawing.Graphics]::FromImage(\$b); \$g.CopyFromScreen(0,0,0,0,\$b.Size); \$b.Save(\$env:SMOKE_SHOT_WIN); \$g.Dispose(); \$b.Dispose()" 2>/dev/null \
    || log "截图跳过（无桌面会话）"
}

if [[ "$SELFTEST" -eq 1 ]]; then
  smoke_self_test_run ""
  exit $?
fi

INSTALL_DIR="$(mktemp -d)/app"
HOME_DIR="$(mktemp -d)"
OUT="$(mktemp)"
WIN_DIR="$(cygpath -w "$INSTALL_DIR" 2>/dev/null || echo "$INSTALL_DIR")"
APP_EXE="$INSTALL_DIR/$APP_NAME.exe"

cleanup() {
  powershell -NoProfile -Command "Stop-Process -Name '$APP_NAME' -Force -ErrorAction SilentlyContinue" >/dev/null 2>&1 || true
  # 安装目录与 home 也要收：只删 $OUT 会留下 mktemp 树（本批补）
  rm -rf "$OUT" "$INSTALL_DIR" "$HOME_DIR"
}
trap cleanup EXIT

echo "== 安装（静默，DIR=${WIN_DIR}）"
# 安装环节取证：Inno 是 GUI 子系统，stdout 恒空——唯一诊断面是 /LOG 安装日志
# （逐文件动作）。安装器后台运行 + 步级超时（首次实跑 88MB 闭包静默装曾 >7min
# 无任何输出，用户终止——卡在哪一步只能靠 /LOG 回答）；超时即 dump 日志尾部 +
# 进程表 fail loud，绝不静默挂死。
WIN_LOG="$(cygpath -w "$HOME_DIR/install.log" 2>/dev/null || echo "$HOME_DIR/install.log")"
# 固定 300s 预算，非可覆写旋钮：本脚本只认 SMOKE_WAIT_SECONDS / SMOKE_SETTLE_SECONDS。
INSTALL_WAIT=300
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
  error "[win] 安装器 ${INSTALL_WAIT}s 未退出（疑似卡住）。install.log 尾部："
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
  error "[win] 安装器退出码 $install_rc 或缺主程序。install.log 尾部："
  tail -40 "$HOME_DIR/install.log" >&2 || true
  exit 1
fi
if [[ ! -f "$INSTALL_DIR/unins000.exe" ]]; then
  # 真安装器语义断言：无卸载器 = 产物是自解压包而非安装器（历史静默降级事故的判别位）
  die "安装后缺 unins000.exe——产物疑似非 Inno 安装器（回退链静默降级？）"
fi
echo "== 安装完成（install.log 尾部留痕）"
tail -3 "$HOME_DIR/install.log" >&2
echo "== 启动冒烟（等①就绪后等导航落定，②保底；窗=${SMOKE_WAIT}s/落定${SETTLE_WAIT}s）"
LOG="$HOME_DIR/logs/host.log"
set +e
smoke_unattended_env "$HOME_DIR"
"$APP_EXE" >"$OUT" 2>&1 &
pid=$!
rc=1
if smoke_wait_ready "$OUT" "$LOG" "$pid" "$HOME_DIR"; then
  rc=0
  smoke_evidence_pass "$OUT" "$LOG" ""
  smoke_shot "smoke-windows.png"
else
  rc=1
  smoke_shot "smoke-windows-fail.png"
  smoke_evidence_fail "$OUT" "$LOG" tail
fi
set -e
if [[ $rc -ne 0 ]]; then
  error "[win] 冒烟失败——${SMOKE_WAIT}s 内未出现 dsh web URL 或引导启动行。stdout 尾部："
  tail -30 "$OUT" >&2 || true
  smoke_shot "smoke-windows-fail.png"
fi
kill "$pid" 2>/dev/null || true
wait "$pid" 2>/dev/null || true
# 日志落盘（W2）：调用方经 SMOKE_LOG_DIR 注入稳定目录（与 SMOKE_SHOT_DIR 同模式），
# CI 传 artifact——host.log 只在文件里全，step 日志只有尾巴。
# 日志落盘（W2）：SMOKE_LOG_DIR 由调用方注入稳定目录，CI 传 artifact。
smoke_dump_logs "smoke-windows" "$OUT" "${LOG:-}"
exit $rc
