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

# signtool 位置：Windows SDK 两条标准路径 + PATH（原先的六路级联收敛为此三路）。
find_signtool() {
  local p
  for p in "/c/Program Files (x86)/Windows Kits/10/bin/"*/x64/signtool.exe \
    "/c/Program Files/Windows Kits/10/bin/"*/x64/signtool.exe; do
    if [[ -f "$p" ]]; then
      echo "$p"
      return 0
    fi
  done
  if command -v signtool >/dev/null 2>&1; then
    command -v signtool
    return 0
  fi
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
