#!/usr/bin/env bash
# prepare-windows-smoke.sh — Windows 冒烟腿的 runner 前置（唯一家）。
# 调用点：package.yml 的 build-windows 腿「冒烟前置」步；本脚本不碰产物，只备 runner 状态。
#
# 1｜Defender 实时防护排除 —— **判门**（失败即红）。冒烟首启要 `npm install -g` 装 dsh 闭包，
#   runner 的实时防护逐文件扫描是这条腿的主要成本（量测与判别见 ADR
#   process/2026-10-01-windows-smoke-npm-install-speedup）。排除面 = npm 下载缓存 +
#   npm 全局前缀 + node 安装目录。判门是刻意的：本步的产出就是「扫描不再拖慢」，排除没生效
#   却继续走，等于这轮提速悄悄不发生。
# 2｜WebView2 运行库先探后装 —— 已登记（注册表 `pv` 非空非 `0.0.0.0`，判据与仓内
#   packaging/windows/installer.iss.in 的 IsWebView2Available 同源）即跳过 Evergreen 的
#   下载+静默安装；未登记才装，下载 3 次重试、失败即红。目录枚举与 `pv` 结论只留痕不判门。
#
# 环境：RUNNER_TEMP（Evergreen 安装器落点；GitHub Actions 注入）。
# 用法: prepare-windows-smoke.sh
# 自测: prepare-windows-smoke.sh --self-test（纯判定夹具，不碰注册表/网络/Defender）
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
source "$SCRIPT_DIR/lib/common.sh"

# Git Bash 会把 /silent /install 当 POSIX 路径转换（cookbook [脚本]；smoke-install-windows.sh
# 的 /VERYSILENT 同款先例），本脚本自带导出，不依赖调用方注入。
export MSYS2_ARG_CONV_EXCL='*'

# ---------------------------------------------------------------- 纯判定（自测面）

# pv 缺失即视为未装：空、纯空白、0.0.0.0 三种形态（同 installer.iss.in 的 IsWebView2Available）。
webview2_missing() { # $1=pv 字符串；0=缺失（需安装）
  local pv="${1//[[:space:]]/}"
  [[ -z "$pv" || "$pv" == "0.0.0.0" ]]
}

# ---------------------------------------------------------------- 平台 IO

# 打印已登记的 WebView2 版本（首个命中即停，无命中打印空串）。
# 探针自身失败按「未装」处理——方向安全（退回 Evergreen 安装＝本步引入前的老行为），失败留痕。
webview2_registered_pv() {
  local probe out=""
  # 单引号 heredoc：PowerShell 的 `$id`/`$paths` 不被 bash 展开（双引号内嵌 PS 会被 $ 展开 +
  # 反斜杠转义层层叠加，是本类探针的历史坑形态）。
  probe="$(cat <<'PS'
$id = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$paths = @(
  "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$id",
  "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$id",
  "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$id"
)
foreach ($p in $paths) {
  try {
    $v = (Get-ItemProperty -Path $p -Name 'pv' -ErrorAction Stop).pv
    if ($v -and $v -ne '0.0.0.0') { Write-Output $v; break }
  } catch { }
}
PS
)"
  out="$(powershell -NoProfile -Command "$probe")" || { log "[win] WebView2 注册表探针失败，按未装处理（退回 Evergreen 安装）"; out=""; }
  printf '%s' "${out//[[:space:]]/}"
}

# 对 npm 首启路径加 Defender 实时防护排除并打印生效面（留痕）。失败即红（die），不吞。
apply_defender_exclusions() {
  local script
  script="$(cat <<'PS'
$ErrorActionPreference = 'Stop'
$dirs = @(
  (Join-Path $env:LOCALAPPDATA 'npm-cache'),
  (Join-Path $env:APPDATA 'npm'),
  'C:\Program Files\nodejs'
)
foreach ($d in $dirs) { Add-MpPreference -ExclusionPath $d -ErrorAction Stop }
'== Defender 实时防护排除面（留痕） =='
(Get-MpPreference).ExclusionPath
'== 实时防护开关（False=开；本步只排除，不改它） =='
(Get-MpPreference).DisableRealtimeMonitoring
PS
)"
  powershell -NoProfile -Command "$script"
}

# WebView2 未注册时的安装支路：Evergreen standalone，下载 3 次重试 + /silent /install。
install_webview2_evergreen() {
  local exe="${RUNNER_TEMP:-/tmp}/MicrosoftEdgeWebview2Setup.exe" i ok=0
  for i in 1 2 3; do
    if curl -sSL --fail --retry 2 -o "$exe" 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'; then
      ok=1
      break
    fi
    # 失败即清残片：留着会被下一轮的下载或循环后的判据当成成功产物。
    rm -f "$exe"
    warn "[win] WebView2 Evergreen 下载失败（第 ${i} 次），10s 后重试"
    sleep 10
  done
  # 判据是「下载成功」而非「文件存在」：curl 中途断流会留下半截文件（exit 18 实测形态），
  # 存在性判据会把坏安装器放行到安装步，把「下载失败」误诊成「安装失败」。
  [[ "$ok" -eq 1 ]] || die "[win] WebView2 Evergreen 下载失败（3 次重试）——MS CDN 抖动按网络 flake 重跑"
  "$exe" /silent /install || die "[win] WebView2 Evergreen 安装失败（exit $?）"
}

# 证据位（不判门）：安装目录版本留痕 + 注册表 pv 结论（传入值须为**当前**登记状态）。
webview2_evidence() { # $1=已登记 pv（可空）
  echo '== WebView2 版本留痕（证据位） =='
  powershell -NoProfile -Command "Get-ChildItem 'C:\Program Files (x86)\Microsoft\EdgeWebView\Application' | Select-Object Name | Format-Table -HideTableHeaders | Out-String -Width 200" \
    || warn "[win] WebView2 安装目录枚举失败（仅证据，不影响冒烟）"
  if [[ -n "$1" ]]; then
    echo "WebView2 pv=$1（注册表已登记）"
  else
    echo "::warning::WebView2 注册表探针未命中（安装器退出码为 0 但 pv 缺失，冒烟仍继续）"
  fi
}

# ---------------------------------------------------------------- 入口

prepare_self_test() { # 纯判定夹具（不碰注册表/网络/Defender）
  local fails=0
  _pv_assert() { # $1=描述 $2=pv $3=期望 missing|present
    local want="$3" got=present
    webview2_missing "$2" && got=missing
    if [[ "$got" == "$want" ]]; then
      echo "ok: $1"
    else
      echo "FAIL: $1（pv='$2' 期望 $want，实得 $got）" >&2
      fails=$((fails + 1))
    fi
  }
  _pv_assert "空 pv 视为未装" "" missing
  _pv_assert "0.0.0.0 视为未装" "0.0.0.0" missing
  _pv_assert "空白 pv 视为未装" "   " missing
  _pv_assert "真实版本视为已装" "154.0.4258.48" present
  if [[ "$fails" -eq 0 ]]; then
    echo "prepare-windows-smoke self-test: PASS"
    return 0
  fi
  echo "prepare-windows-smoke self-test: FAIL（${fails} 项）" >&2
  return 1
}

main() {
  local pv
  log "[win] Defender 实时防护排除（npm 首启装包提速）：应用中"
  apply_defender_exclusions \
    || die "[win] Defender 排除应用失败（Add-MpPreference：runner 镜像变更 / tamper protection？）——排除没生效＝本轮提速没发生"
  log "[win] Defender 排除已生效"

  pv="$(webview2_registered_pv)"
  if webview2_missing "$pv"; then
    log "[win] WebView2 未注册，安装 Evergreen"
    install_webview2_evergreen
    # 装完复探：装前值不能当装后结论，否则安装支路的证据位恒报「未命中」。
    pv="$(webview2_registered_pv)"
  else
    log "[win] WebView2 已由 runner 镜像提供（pv=${pv}），跳过 Evergreen 下载/安装"
  fi
  webview2_evidence "$pv"
}

case "${1:-}" in
  --self-test)
    prepare_self_test
    exit $?
    ;;
  '')
    main
    ;;
  *)
    error "未知参数: $1（用法：prepare-windows-smoke.sh [--self-test]）"
    exit 2
    ;;
esac
