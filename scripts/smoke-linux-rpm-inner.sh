#!/usr/bin/env bash
# smoke-linux-rpm-inner.sh — rpm 容器腿的容器内脚本（由 smoke-linux-rpm.sh 经 docker 起）。
# 单独成文件而非 heredoc：①可被 shellcheck 与评审读到（heredoc 内文是不透明的字面块）；
# ②免掉「宿主变量在引号界定符下保持字面」的坑位。
#
# 判定语义与宿主腿同一实现（scripts/lib/smoke-wait-lib.sh + smoke-verdict-lib.sh）：
# 容器无 X，落定/裁决门照跑（行为三信号与显示无关），无截图（见证门不适用）。
# 刻意不带 `set -e`：dnf 失败要走显式分支打印包安装诊断，而非无声退出。
# verify-shell-standards: no-errexit dnf 失败走显式分支打印诊断，-e 会让失败无声退出
#
# 环境（宿主经 docker -e 注入；本地自测直跑）：
#   SMOKE_PKG_NAME    /pkg 下的 rpm 文件名（真实腿必填）
#   SMOKE_APP_BIN     被测可执行（真实腿 /usr/bin/deepseek-harness-desktop）
#   APP_TIMEOUT       timeout 秒（宿主传 SMOKE_WAIT + 20）
#   SMOKE_WAIT_SECONDS / SMOKE_SETTLE_SECONDS  等待窗/落定窗覆写（由库的 smoke_resolve_windows 读）
#   SMOKE_LIB_DIR     共享库目录（容器内 /smoke-lib；本地自测传仓库 scripts/lib）
#   SMOKE_SKIP_INSTALL=1  跳过 dnf 安装（`--self-test` 自动置位）
# 用法（容器内）: bash /smoke-inner.sh   自测: bash scripts/smoke-linux-rpm-inner.sh --self-test
set -uo pipefail

SMOKE_LIB_DIR="${SMOKE_LIB_DIR:-/smoke-lib}"

# 载入共享库并解析窗口（唯一入口）：真实腿用容器挂载点，本地自测用仓库 scripts/lib。
# 写成函数而非顶层 source：自测要能在同一次运行里换库目录（顶层 source 在参数解析前就跑了）。
smoke_rpm_init() { # $1=库目录（缺省 SMOKE_LIB_DIR）
  # shellcheck source=/dev/null
  source "${1:-$SMOKE_LIB_DIR}/smoke-verdict-lib.sh"
  # 窗口解析必须先于一切等待（漏调即 set -u 炸——见 smoke-wait-lib.sh 的注释）
  smoke_resolve_windows
}

# 一条腿：装包（可跳过）→ 起壳 → 等判定信号 → 结论行。$1=日志路径
smoke_rpm_leg() {
  local log_path="$1" home pid rc=1
  # OUT/LOG 同指一文件（宿主侧同款理由：容器内 stdout 即全量日志，存活门须读得到）。
  # shellcheck disable=SC2034  # OUT/LOG 是共享库的调用方契约全局（本文件只见赋值）
  OUT="$log_path"
  # shellcheck disable=SC2034  # LOG 同上（同指一文件）
  LOG="$log_path"

  if [[ "${SMOKE_SKIP_INSTALL:-0}" != "1" ]]; then
    if ! dnf install -y --setopt=install_weak_deps=False "/pkg/$SMOKE_PKG_NAME" >"$log_path" 2>&1; then
      error "[rpm] dnf 安装失败（显式 Requires 不满足或包损坏）："
      tail -30 "$log_path" >&2
      return 1
    fi
  fi
  home="$(mktemp -d)"
  DSH_DESKTOP_DSH_HOME="$home" timeout "$APP_TIMEOUT" "$SMOKE_APP_BIN" >"$log_path" 2>&1 &
  pid=$!
  smoke_wait_ready "$log_path" "$log_path" "$pid" "$home" && rc=0
  if [[ $rc -eq 0 ]]; then
    smoke_verdict "$log_path" "$log_path" ""
  else
    smoke_evidence_fail "$log_path" "$log_path" full
  fi
  kill "$pid" 2>/dev/null
  rm -rf "$home"
  return $rc
}

# ── 自测（离线；本机可跑，CI docs job 每次 push 跑）──────────────────────────
# 覆盖容器腿的**变量契约**与整条判定链：本机无 docker，真实腿只在 tag/dispatch 上跑，
# 而「改名后窗口变量漏接线」这类缺陷曾让整条腿一跑就死（set -u 下 SMOKE_WAIT 未绑定）。
# 夹具用假壳脚本产出 ①+铸币+代理流量三类信号，断言「落定 → 结论行 → 退出码 0」。
smoke_rpm_selftest() {
  local tmp fails=0 rc=0 fake out lib
  lib="$(cd "$(dirname "${BASH_SOURCE[0]}")/lib" && pwd)"
  smoke_rpm_init "$lib"
  tmp="$(mktemp -d)"
  fake="$tmp/fake-shell"
  {
    printf '#!/bin/sh\n'
    printf 'printf "%%s\\n" "[host] dsh web = http://127.0.0.1:1/"\n'
    printf 'printf "%%s\\n" "[shell] 铸币：token 跳 → 303（set-cookie=[c] 共1个；http://127.0.0.1:1）"\n'
    printf 'printf "%%s\\n" "[shell] 代理回包：200 application/json 100字节（POST /api/a）"\n'
    printf 'printf "%%s\\n" "[shell] 代理流转：200 text/event-stream（GET /plugins/events）"\n'
    printf 'printf "%%s\\n" "[shell] 代理回包：200 application/json 200字节（POST /api/b）"\n'
    printf 'sleep 30\n'
  } >"$fake"
  chmod +x "$fake"
  out="$(SMOKE_LIB_DIR="$lib" SMOKE_SKIP_INSTALL=1 SMOKE_APP_BIN="$fake" APP_TIMEOUT=20 \
    SMOKE_WAIT_SECONDS=10 SMOKE_SETTLE_SECONDS=5 bash "${BASH_SOURCE[0]}" 2>&1)" || rc=$?
  if [[ $rc -eq 0 ]]; then
    echo "ok: 容器腿端到端（落定 → 退出码 0）"
  else
    echo "FAIL: 容器腿端到端 rc=$rc" >&2
    echo "$out" >&2
    fails=$((fails + 1))
  fi
  if [[ "$out" == *"SMOKE_VERDICT=full-chain"* ]]; then
    echo "ok: 结论行 full-chain"
  else
    echo "FAIL: 缺结论行（$out）" >&2
    fails=$((fails + 1))
  fi
  # 窗口旋钮 → 库全局（上一条即以 SMOKE_WAIT_SECONDS=10 驱动整腿）
  SMOKE_WAIT_SECONDS=7 smoke_resolve_windows
  if [[ "$SMOKE_WAIT" -eq 7 ]]; then
    echo "ok: SMOKE_WAIT_SECONDS → SMOKE_WAIT"
  else
    echo "FAIL: 窗口旋钮未接线（SMOKE_WAIT=$SMOKE_WAIT）" >&2
    fails=$((fails + 1))
  fi
  rm -rf "$tmp"
  if [[ $fails -eq 0 ]]; then
    echo "self-test: PASS"
    return 0
  fi
  echo "self-test: FAIL（$fails 项）" >&2
  return 1
}

if [[ "${1:-}" == "--self-test" ]]; then
  smoke_rpm_selftest
  exit $?
fi

# 真实容器腿：日志落盘走 EXIT trap（$log_path 先定义；$home 可能未建，用 :- 守 set -u）
smoke_rpm_init
log_path=/tmp/smoke.log
home=""
trap '[[ -d /smokelogs ]] && { cp "$log_path" /smokelogs/smoke-linux-rpm.log 2>/dev/null || true; [[ -f "${home:-}/logs/host.log" ]] && cp "${home:-}/logs/host.log" /smokelogs/smoke-linux-rpm-host.log 2>/dev/null || true; } || true' EXIT
smoke_rpm_leg "$log_path"
exit $?
