#!/usr/bin/env bash
# dev-sign.sh — 开发/内部自签（唯一家）：macOS `codesign` 与 Windows `signtool` 两条腿。
# 从 package-*.sh 抽出（ADR script-layer-consolidation）：发布脚本里只留一行显式转调，
# 签名与自签证书创建的逻辑集中在此，发布路径不再夹带调试块。
# 边界：自签/ad-hoc 只消除**本机/内部**的「来源不明/未知发布者」告警，不消除终端用户的
# Gatekeeper/SmartScreen 告警——免费受信签名不存在。
#
# 用法:
#   scripts/dev-sign.sh macos <app bundle 或可执行文件>
#   scripts/dev-sign.sh windows <exe>
#   scripts/dev-sign.sh --self-test        # 离线夹具（注入 codesign/signtool/pwsh 桩）
# 环境: MACOS_SIGN_IDENTITY（macOS 身份，缺省 `-` = ad-hoc）
set -euo pipefail

# shellcheck source=lib/common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/common.sh"
PLATFORM="${1:-}"
TARGET="${2:-}"
usage() {
  cat <<'EOF'
用法: dev-sign.sh <macos|windows> <目标>
  macos   对 .app bundle 或可执行文件做 codesign（身份取 MACOS_SIGN_IDENTITY，缺省 ad-hoc）
  windows 对 exe 做 Authenticode 自签（CurrentUser\My 自签证书，缺则自动创建）
  --self-test 离线夹具（桩 codesign/signtool/pwsh，钉住两条腿的调用面）
EOF
}

sign_macos() {
  local identity="${MACOS_SIGN_IDENTITY:--}"
  command -v codesign >/dev/null 2>&1 || die "缺 codesign（仅 macOS 可用）"
  echo "== codesign 自签（identity=${identity}）: $TARGET"
  codesign --force --deep --sign "$identity" "$TARGET" || die "codesign 自签失败"
  codesign --verify --deep --strict "$TARGET" || die "codesign 校验失败"
  echo "  ad-hoc/自签通过（对终端用户 Gatekeeper 无效，属内部/开发验证）"
}

# signtool 位置：PATH 优先（显式环境可覆盖，自测桩也靠它遮蔽），否则 Windows SDK
# 两条标准路径。
find_signtool() {
  local p
  if command -v signtool >/dev/null 2>&1; then
    command -v signtool
    return 0
  fi
  for p in "/c/Program Files (x86)/Windows Kits/10/bin/"*/x64/signtool.exe \
    "/c/Program Files/Windows Kits/10/bin/"*/x64/signtool.exe; do
    [[ -f "$p" ]] && { echo "$p"; return 0; }
  done
  return 1
}

# 自签代码签名证书（CurrentUser\My，缺则建，3 年有效）。自签证书不被终端用户信任，
# 不消除 SmartScreen 告警，仅治本机/内部。
ensure_self_sign_cert() {
  local ps="powershell"
  command -v pwsh >/dev/null 2>&1 && ps="pwsh"
  "$ps" -NoProfile -Command "\$c = Get-ChildItem Cert:\CurrentUser\My | Where-Object { \$_.Subject -like '*DeepSeek Harness Desktop Dev*' } | Select-Object -First 1; if (-not \$c) { New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=DeepSeek Harness Desktop Dev' -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(3) | Out-Null; Write-Output 'created' } else { Write-Output 'exists' }" 2>&1 | head -5 || true
}

sign_windows() {
  local st
  st="$(find_signtool)" || die "缺 signtool（Windows SDK）"
  ensure_self_sign_cert
  echo "   signtool: $st"
  "$st" sign /fd SHA256 /s My /n "DeepSeek Harness Desktop Dev" "$TARGET" 2>&1 | head -20 || die "signtool 签名失败: $TARGET"
  echo "   已签（自签，仅内部/开发）: $TARGET"
}

# 离线夹具：注入 codesign/signtool/pwsh 桩（记录 argv），在不具备真工具的机器上
# 把两条腿的调用面钉住——否则签名腿只有真机手动跑才被执行过。
dev_sign_self_test() {
  common_tmp_trap
  local tmp bin calls fails=0 rc out target
  tmp="$(common_tmp_dir)"
  bin="$tmp/bin"
  calls="$tmp/calls"
  mkdir -p "$bin"
  target="$tmp/target.bin"
  printf 'fake\n' >"$target"
  : >"$calls"
  _stub() { # $1=名字 $2=退出码
    printf '#!/usr/bin/env bash\nprintf "%%s %%s\\n" "%s" "$*" >>"%s"\nexit %s\n' "$1" "$calls" "$2" >"$bin/$1"
    chmod +x "$bin/$1"
  }
  _expect() { # $1=描述 $2=期望（数字精确比较；非0=任意失败） $3=实得 rc
    if [[ ( "$2" == 非0 && "$3" -eq 0 ) || ( "$2" != 非0 && "$3" -ne "$2" ) ]]; then
      echo "FAIL: $1（rc=$3）" >&2
      fails=$((fails + 1))
    else
      echo "ok: $1"
    fi
  }
  _run() { # $1..=argv；结果写 rc/out
    rc=0
    out="$(PATH="$bin:$PATH" "$@" 2>&1)" || rc=$?
  }
  _calls_has() { # $1=描述 $2=期望调用行
    if grep -qF -- "$2" "$calls"; then
      echo "ok: $1"
    else
      echo "FAIL: $1（见 $calls）" >&2
      fails=$((fails + 1))
    fi
  }

  _stub codesign 0
  _stub signtool 0
  _stub pwsh 0

  _run bash "${BASH_SOURCE[0]}" macos "$target"
  _expect "macos 默认 ad-hoc 签名放行" 0 "$rc"
  _calls_has "macos 调用面（--sign - 后跟 --verify）" "codesign --force --deep --sign - $target"
  _calls_has "macos verify" "codesign --verify --deep --strict $target"

  : >"$calls"
  MACOS_SIGN_IDENTITY="Dev Identity" _run bash "${BASH_SOURCE[0]}" macos "$target"
  _expect "macos 指定身份签名放行" 0 "$rc"
  _calls_has "MACOS_SIGN_IDENTITY 透传" "codesign --force --deep --sign Dev Identity $target"

  _stub codesign 1
  _run bash "${BASH_SOURCE[0]}" macos "$target"
  _expect "codesign 失败即 fail loud" 非0 "$rc"
  [[ "$out" == *"codesign 自签失败"* ]] || { echo "FAIL: codesign 失败缺诊断" >&2; fails=$((fails + 1)); }
  _stub codesign 0

  : >"$calls"
  _run bash "${BASH_SOURCE[0]}" windows "$target"
  _expect "windows 自签放行" 0 "$rc"
  _calls_has "signtool 调用面" "signtool sign /fd SHA256 /s My /n DeepSeek Harness Desktop Dev $target"
  _calls_has "自签证书路径先试 pwsh" "pwsh"

  _stub signtool 1
  _run bash "${BASH_SOURCE[0]}" windows "$target"
  _expect "signtool 失败即 fail loud" 非0 "$rc"
  [[ "$out" == *"signtool 签名失败"* ]] || { echo "FAIL: signtool 失败缺诊断" >&2; fails=$((fails + 1)); }
  _stub signtool 0

  _run bash "${BASH_SOURCE[0]}" macos "$tmp/nope"
  _expect "缺目标即 fail loud" 非0 "$rc"
  _run bash "${BASH_SOURCE[0]}"
  _expect "缺参数走 usage（exit 2）" 2 "$rc"
  _run bash "${BASH_SOURCE[0]}" aix "$target"
  _expect "未知平台即 fail loud" 非0 "$rc"

  if [[ $fails -eq 0 ]]; then
    echo "self-test: PASS"
    return 0
  fi
  echo "self-test: FAIL（$fails 项）" >&2
  return 1
}

if [[ "${1:-}" == "--self-test" ]]; then
  dev_sign_self_test
  exit $?
fi

case "$PLATFORM" in
  macos)
    [[ -e "$TARGET" ]] || die "目标不存在: $TARGET"
    sign_macos
    ;;
  windows)
    [[ -f "$TARGET" ]] || die "目标不存在: $TARGET"
    sign_windows
    ;;
  -h | --help | "") usage; exit 2 ;;
  *) usage >&2; die "未知平台: $PLATFORM" ;;
esac
