#!/usr/bin/env bash
# verify-shell-standards.sh — 脚本层规范门禁（docs/script-standards.md 的机器强制面）。
# 覆盖面：scripts/**/*.sh（含 scripts/lib，库另按 S3 判形）与 .githooks/*。判据六条，
# 每条都有能失败的路径（--self-test 逐条钉住）：
#   S1 入口脚本须 `set -euo pipefail`（豁免：`set -uo pipefail` + 行首注释
#      `# verify-shell-standards: no-errexit <理由>`）
#   S2 单文件 ≤250 行（豁免：行首注释 `# verify-shell-standards: allow-long <理由>`）
#   S3 共享库（scripts/lib/*.sh）不得有可执行位、不得自设 shell 选项（会改调用方行为）
#   S4 旋钮声明：`${NAME:-}`/`${NAME:?}` 读到的变量须有声明（本文件头部注释 / CI 工作流 /
#      docs / 白名单）——「没人设也没人写」的旋钮是死旋钮（A6 实测形态）
#   S5 共享库单源：scripts/lib/*.sh 里定义的函数不得在别的脚本重定义（A5 影子副本形态）
#   S6 `shellcheck -S warning` 零告警（版本钉在 docs/script-standards.md「工具版本」）
# 用法: verify-shell-standards.sh [--root <dir>] [--self-test]
# Exit code 0 = pass, 1 = violations.
set -euo pipefail

SIZE_LIMIT=250
KNOB_WHITELIST=""  # 旋钮白名单（每条附理由；尽量留空——能写进头部注释或 docs 的就不该进这里）
SELF_TEST=0

usage() {
  cat <<'EOF'
用法: verify-shell-standards.sh [--root <dir>] [--self-test]
  --root <dir>   检查这棵树（缺省 = 本脚本所在仓库；夹具自测用）
  --self-test    离线夹具：逐条判据的判红/放行两侧
EOF
}

list_checked_scripts() { # $1=root：受检脚本（scripts/**.sh 含 lib + .githooks/*；库同受 S2/S4/S6）
  find "$1/scripts" -type f -name '*.sh' -not -path '*/scripts/lib/*' 2>/dev/null | sort
  find "$1/.githooks" -type f 2>/dev/null | sort
  find "$1/scripts/lib" -type f -name '*.sh' 2>/dev/null | sort
}
list_non_lib_scripts() {
  find "$1/scripts" -type f -name '*.sh' -not -path '*/scripts/lib/*' 2>/dev/null | sort
  find "$1/.githooks" -type f 2>/dev/null | sort
}
list_libs() { find "$1/scripts/lib" -type f -name '*.sh' 2>/dev/null | sort; }

VIOLATIONS=0
violation() {
  printf 'FAIL: %s\n' "$*"
  VIOLATIONS=$((VIOLATIONS + 1))
}

# S1：入口脚本的错误处置纪律（fail loud）。库文件不在本判据面（S3 管其形态）。
check_errexit() { # $1=文件
  grep -qE '^set -euo pipefail$' "$1" && return 0
  if grep -qE '^set -uo pipefail$' "$1" && grep -qE 'verify-shell-standards: no-errexit .+' "$1"; then
    return 0
  fi
  violation "$1: 缺 'set -euo pipefail'（豁免须 'set -uo pipefail' + 行内 'verify-shell-standards: no-errexit <理由>'）"
}

# S2：尺寸闸——超限即拆（Google「>100 行就不该用 shell」的本仓折中值：250）。
check_size() { # $1=文件
  local lines
  lines="$(wc -l <"$1")"
  (( lines > SIZE_LIMIT )) || return 0
  grep -qE '^#[[:space:]]*verify-shell-standards: allow-long .+' "$1" && return 0
  violation "$1: ${lines} 行 > ${SIZE_LIMIT} 行（拆文件；豁免须行内 'verify-shell-standards: allow-long <理由>'）"
}

# S3：库文件形态——不可执行、不自设 shell 选项（两者都会让「库」变成隐性入口/改调用方语义）。
check_lib_shape() { # $1=文件
  [[ -x "$1" ]] && violation "$1: 库文件不得带可执行位（source 用，非入口；chmod -x）"
  grep -qE '^set -' "$1" && violation "$1: 库文件不得自设 shell 选项（会改调用方行为；-euo 归入口脚本）"
  return 0
}

# S4：旋钮声明。声明面＝本文件头部注释（前 70 行）／CI 工作流与本地 hooks／docs／白名单。
knob_declared() { # $1=名字 $2=文件 $3=root
  # 头部**注释**里的声明（代码里的读取本身不算声明——否则「读它」即「声明它」，判据恒真）
  head -n 70 "$2" | grep -E '^[[:space:]]*#' | grep -qF "$1" && return 0
  grep -rqF "$1" "$3/.github" "$3/docs" "$3/README.md" "$3/README.en.md" 2>/dev/null && return 0
  [[ -n "$KNOB_WHITELIST" ]] && [[ " $KNOB_WHITELIST " == *" $1 "* ]]
}

check_knobs() { # $1=文件 $2=root
  local name
  while IFS= read -r name; do
    [[ -n "$name" ]] || continue
    knob_declared "$name" "$1" "$2" && continue
    violation "$1: 旋钮 \$${name} 无声明（写进本文件头部注释／CI 环境变量／docs，或入白名单附理由）"
  done < <(grep -oE '\$\{[A-Z][A-Z0-9_]*:[-?]' "$1" | sed -E 's/^\$\{//; s/:[-?]$//' | sort -u)
}

# S5：共享库单源——库函数的第二份定义即影子副本（A5 实证：副本会与库分叉且更脆）。
check_lib_single_home() { # $1=root
  local name f
  while IFS= read -r name; do
    [[ -n "$name" ]] || continue
    while IFS= read -r f; do
      grep -qE "^[[:space:]]*(function[[:space:]]+)?${name}\(\)" "$f" || continue
      violation "$f: 重定义了共享库函数 ${name}()（唯一家在 scripts/lib/*.sh）"
    done < <(list_non_lib_scripts "$1")
  done < <(while IFS= read -r f; do grep -hoE '^[[:space:]]*(function[[:space:]]+)?[a-z_][a-z0-9_]*\(\)' "$f"; done < <(list_libs "$1") 2>/dev/null | sed -E 's/^[[:space:]]*(function[[:space:]]+)?//; s/\(\)$//' | sort -u)
}

# S6：shellcheck（唯一家判据：静态检查全交工具，本脚本不重造检查项）。
# 查找顺序 = 本仓 .cache/bin（install-linters.sh 钉的版本，本地与 CI 同版）→ PATH。
# 用门禁自身仓库而非受检树：夹具模式（--root）与真实运行拿到同一个工具。
shellcheck_bin() {
  if [[ -x "$SELF_ROOT/.cache/bin/shellcheck" ]]; then
    printf '%s' "$SELF_ROOT/.cache/bin/shellcheck"
    return 0
  fi
  command -v shellcheck 2>/dev/null
}

run_checks() { # $1=受检树根
  local root="$1" sc f
  local -a files=()
  while IFS= read -r f; do files+=("$f"); done < <(list_checked_scripts "$root")
  [[ ${#files[@]} -gt 0 ]] || violation "未找到任何脚本（$root/scripts 与 $root/.githooks 皆空？）"
  while IFS= read -r f; do check_errexit "$f"; done < <(list_non_lib_scripts "$root")
  for f in "${files[@]}"; do check_size "$f"; check_knobs "$f" "$root"; done
  while IFS= read -r f; do check_lib_shape "$f"; done < <(list_libs "$root")
  check_lib_single_home "$root"
  if ! sc="$(shellcheck_bin)"; then
    violation "缺 shellcheck —— 门禁无法执行（跑 scripts/install-linters.sh，版本见 docs/script-standards.md）"
  elif ! "$sc" -S warning "${files[@]}"; then
    violation "shellcheck -S warning 有告警（见上；豁免用行内 '# shellcheck disable=SCxxxx # 理由'）"
  fi

  if [[ $VIOLATIONS -gt 0 ]]; then
    printf 'FAIL: 脚本规范门禁 %d 项违规（规范见 docs/script-standards.md）\n' "$VIOLATIONS"
    return 1
  fi
  printf 'OK: 脚本规范门禁（%d 文件：S1-S6 全过）\n' "${#files[@]}"
  return 0
}

# 夹具自测：逐条判据的判红侧与放行侧（只做判据，不装工具）。
smoke_standards_self_test() {
  local fix fails=0 out
  fix="$(mktemp -d)"
  mkdir -p "$fix/scripts/lib"
  # 合规样板 + 三条豁免侧
  # 旋钮模板用拼接生成：本门禁会扫自己，逐字写 `${X:-` 会被自家扫描器当成死旋钮。
  knob_open='${'
  {
    printf '#!/usr/bin/env bash\n'
    printf '# good.sh — 夹具：旋钮 GOOD_KNOB 可覆写（头部即声明面）。\n'
    printf 'set -euo pipefail\n'
    printf 'echo "%sGOOD_KNOB:-1}"\n' "$knob_open"
  } >"$fix/scripts/good.sh"
  cat >"$fix/scripts/no-errexit.sh" <<'EOF'
#!/usr/bin/env bash
set -uo pipefail
echo hi
EOF
  cat >"$fix/scripts/no-errexit-exempt.sh" <<'EOF'
#!/usr/bin/env bash
# 豁免理由：dnf 失败要走显式分支打印诊断，而非无声退出。
# verify-shell-standards: no-errexit 显式分支取代 -e
set -uo pipefail
echo hi
EOF
  {
    printf '#!/usr/bin/env bash\nset -euo pipefail\n'
    for _ in $(seq 1 260); do printf '# filler\n'; done
  } >"$fix/scripts/long.sh"
  {
    printf '#!/usr/bin/env bash\n'
    printf '# verify-shell-standards: allow-long 夹具：豁免侧\n'
    printf 'set -euo pipefail\n'
    for _ in $(seq 1 260); do printf '# filler\n'; done
  } >"$fix/scripts/long-exempt.sh"
  {
    printf '#!/usr/bin/env bash\n'
    printf 'set -euo pipefail\n'
    printf 'echo "%sDEAD_KNOB:-1}"\n' "$knob_open"
  } >"$fix/scripts/undeclared-knob.sh"
  cat >"$fix/scripts/lib/thing.sh" <<'EOF'
#!/usr/bin/env bash
thing_fn() { echo hi; }
EOF
  cp "$fix/scripts/lib/thing.sh" "$fix/scripts/lib/executable-lib.sh"
  chmod +x "$fix/scripts/lib/executable-lib.sh"
  cat >"$fix/scripts/lib/with-set.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
EOF
  cat >"$fix/scripts/redefines.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
thing_fn() { echo shadow; }
EOF
  cat >"$fix/scripts/bad-shellcheck.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
unset v
echo $v
EOF

  out="$(run_checks "$fix" 2>&1)" && { echo "FAIL: 夹具应判红，实得 rc=0" >&2; fails=$((fails + 1)); } || true
  _st_expect() { # $1=名字 $2=应出现的串 $3=不应出现的串
    if ! grep -qF "$2" <<<"$out"; then
      echo "FAIL: $1（未报出：$2）" >&2
      fails=$((fails + 1))
      return
    fi
    if [[ -n "$3" ]] && grep -qF "$3" <<<"$out"; then
      echo "FAIL: $1（误报：$3）" >&2
      fails=$((fails + 1))
      return
    fi
    echo "ok: $1"
  }
  _st_expect "S1 缺 set -euo pipefail 判红" "no-errexit.sh: 缺 'set -euo pipefail'" "no-errexit-exempt.sh: 缺"
  _st_expect "S2 超尺寸判红" "long.sh: 262 行 > 250 行" "long-exempt.sh: 262"
  _st_expect "S3 库带可执行位判红" "executable-lib.sh: 库文件不得带可执行位" ""
  _st_expect "S3 库自设 shell 选项判红" "with-set.sh: 库文件不得自设 shell 选项" ""
  _st_expect "S4 死旋钮判红" 'undeclared-knob.sh: 旋钮 $DEAD_KNOB 无声明' 'good.sh: 旋钮 $GOOD_KNOB'
  _st_expect "S5 影子副本判红" "redefines.sh: 重定义了共享库函数 thing_fn()" ""
  if shellcheck_bin >/dev/null 2>&1; then
    _st_expect "S6 shellcheck 告警判红" "bad-shellcheck.sh" ""
  else
    echo "skip: 无 shellcheck，跳过 S6 夹具"
  fi
  rm -rf "$fix"
  if [[ $fails -eq 0 ]]; then
    echo "self-test: PASS"
    return 0
  fi
  echo "self-test: FAIL（$fails 项）" >&2
  return 1
}

SELF_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ROOT="$SELF_ROOT"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --root) ROOT="${2:?--root 需要值}"; shift 2 ;;
    --self-test) SELF_TEST=1; shift ;;
    -h | --help) usage; exit 0 ;;
    *) usage >&2; echo "FAIL: 未知参数 $1" >&2; exit 2 ;;
  esac
done
root="$ROOT"

if [[ $SELF_TEST -eq 1 ]]; then
  smoke_standards_self_test
  exit $?
fi
run_checks "$root"
