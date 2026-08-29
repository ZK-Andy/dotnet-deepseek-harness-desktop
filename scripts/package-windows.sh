#!/usr/bin/env bash
# package-windows.sh — 从 .NET publish 输出打 Windows 安装器（exe，Inno Setup 唯一）。
# Inno 编译失败必须 fail loud——绝不静默降级（历史教训：Git Bash 把 iscc 的 /Q 当
# POSIX 路径转换成第二个脚本文件名致编译恒失败，NSIS/SFX 回退链把失败吞成
# 「成功」，Windows 发布资产实际长期为 7z 自解压包而非安装器，2026-08-29 冒烟实锤）。
# online-first（ADR online-first-unbundled-runtime）：包只带壳 + 安装器自带插件资源
# （resources/plugins/dsh-desktop-companion.tgz）；运行时由首启引导下载，不再捆绑闭包。
# 布局：publish 全量 + resources/plugins
# 用法：
#   scripts/package-windows.sh [publish_dir]
#   scripts/package-windows.sh --stage-only [dir]
# 环境：VERSION、ARCH（x64/arm64，现仅 x64 有完整测试）
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ARG1="${1:-}"
STAGE_ONLY=0
if [[ "$ARG1" == "--stage-only" ]]; then STAGE_ONLY=1; ARG1="${2:-}"; fi

ARCH_RAW="${ARCH:-x64}"
case "$ARCH_RAW" in
  x64|amd64|x86_64) ARCH="x64"; RID="win-x64"; OUT_SUFFIX="win-x64" ;;
  arm64|aarch64) ARCH="arm64"; RID="win-arm64"; OUT_SUFFIX="win-arm64" ;;
  *) echo "error: 不支持 ARCH=$ARCH_RAW" >&2; exit 1 ;;
esac

PUBLISH_DIR="${ARG1:-$ROOT/artifacts/publish-$RID}"
VERSION="${VERSION:-0.1.0}"
OUT="$ROOT/artifacts/$OUT_SUFFIX"
STAGE="$OUT/stage/DeepSeek.Harness.Desktop"

[[ -d "$PUBLISH_DIR" ]] || { echo "error: publish 目录不存在: $PUBLISH_DIR" >&2; exit 1; }

echo "== 组装 staging: $STAGE"
rm -rf "$STAGE" && mkdir -p "$STAGE"
cp -r "$PUBLISH_DIR/." "$STAGE/"

# 安装器自带插件资源：companion tgz 从仓库源码现打并校验（fail loud）。
# 不再捆绑运行时闭包——首启引导负责 Node/dsh 安装（ADR online-first-unbundled-runtime）。
mkdir -p "$STAGE/resources/plugins"
bash "$ROOT/scripts/build-companion-tgz.sh" "$STAGE/resources/plugins/dsh-desktop-companion.tgz"
# 闭包残留检测：resources/runtime 出现即打包漂移（旧缓存/手工产物混入），fail loud
if [[ -e "$STAGE/resources/runtime" ]]; then
  echo "error: staging 出现 resources/runtime（闭包已退役，属打包漂移）" >&2
  exit 1
fi
# 布局断言（批次一）：staging 是 Inno [Files] 的唯一内容源，断言 staging 即断言安装器——
# 插件 tgz 存在且过名称/体积关、无闭包残留，缺一 fail loud
bash "$ROOT/scripts/verify-package-layout.sh" --target "$STAGE"

echo "== staging 体积: $(du -sh "$STAGE" | cut -f1)"
if [[ $STAGE_ONLY -eq 1 ]]; then
  find "$STAGE" -maxdepth 2 -type d | sort | head -20 || true
  ls -lh "$STAGE/resources/plugins/" 2>&1 | head -3 || echo "plugins 缺失"
  exit 0
fi

mkdir -p "$OUT"
# 自签（可选，仅内部/开发验证用）——显式 SELF_SIGN=1 才启用，不默认打扰发布。
# 用 CurrentUser\\My 里的自签代码签名证书（缺则自动建）+ signtool 签 Authenticode。
# 注意：自签证书不被终端用户信任，不消除 SmartScreen「未知发布者」告警，仅治本机/内部。
find_signtool() {
  local p
  for p in /c/Program\ Files\ \(x86\)/Windows\ Kits/10/bin/*/x64/signtool.exe \
           /c/Program\ Files/Windows\ Kits/10/bin/*/x64/signtool.exe; do
    if [[ -f "$p" ]]; then echo "$p"; return 0; fi
  done
  if command -v signtool >/dev/null 2>&1; then command -v signtool; return 0; fi
  return 1
}
ensure_self_sign_cert() {
  local ps="powershell"; command -v pwsh >/dev/null 2>&1 && ps="pwsh"
  "$ps" -NoProfile -Command "\$c = Get-ChildItem Cert:\CurrentUser\My | Where-Object { \$_.Subject -like '*DeepSeek Harness Desktop Dev*' } | Select-Object -First 1; if (-not \$c) { New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=DeepSeek Harness Desktop Dev' -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(3) | Out-Null; Write-Output 'created' } else { Write-Output 'exists' }" 2>&1 | head -5 || true
}
sign_windows() {
  local target="$1" st
  st="$(find_signtool)" || { echo "error: SELF_SIGN=1 但缺 signtool（Windows SDK）" >&2; exit 1; }
  ensure_self_sign_cert
  echo "   signtool: $st"
  "$st" sign /fd SHA256 /s My /n "DeepSeek Harness Desktop Dev" "$target" 2>&1 | head -20 || { echo "error: signtool 签名失败: $target" >&2; exit 1; }
  echo "   已签（自签，仅内部/开发）: $target"
}
if [[ "${SELF_SIGN:-0}" == "1" ]] && [[ -f "$STAGE/DeepSeek.Harness.Desktop.exe" ]]; then
  sign_windows "$STAGE/DeepSeek.Harness.Desktop.exe"
fi

# 单一安装器产物（不再单独产出便携 zip——见 .agent notes：省去对 1.5GB 闭包的重复压缩）。
# 命名含 windows 标识，避免与 macOS/dmg 同名冲突。
INSTALLER="$OUT/DeepSeek.Harness.Desktop_${VERSION}_windows-${ARCH}-setup.exe"
rm -f "$INSTALLER"

# 安装器 exe（Windows 期待 exe 安装器；Linux 已有 deb/rpm，macOS 已有 dmg）
# Inno Setup 6 唯一安装器链（缺工具/编译失败即 fail loud，无静默回退）。
# 坑位（2026-08-29 实锤）：Git Bash 会把 iscc 的 /Q 开关当 POSIX 路径转换成第二个
# 「脚本文件名」（iscc 报 You may not specify more than one script filename），
# 须 MSYS2_ARG_CONV_EXCL 排除 + iss 路径 cygpath -w 显式给 Windows 形态。
create_installer_exe() {
  local staging="$1" installer="$2"
  local staging_win installer_win out_dir iss_file iss_file_win
  # 转 Windows 风格供 Inno Setup（Git Bash 下用 cygpath）
  if command -v cygpath >/dev/null 2>&1; then
    staging_win="$(cygpath -w "$staging" 2>/dev/null || echo "$staging")"
    installer_win="$(cygpath -w "$installer" 2>/dev/null || echo "$installer")"
    out_dir="$(cygpath -w "$(dirname "$installer")" 2>/dev/null || echo "$(dirname "$installer")")"
  else
    staging_win="$staging"
    installer_win="$installer"
    out_dir="$(dirname "$installer")"
  fi
  local iss_base="$(basename "$installer" .exe)"
  local iscc=""
  for p in "/c/Program Files (x86)/Inno Setup 6/ISCC.exe" "/c/Program Files/Inno Setup 6/ISCC.exe" "C:\\Program Files (x86)\\Inno Setup 6\\ISCC.exe" "C:\\Program Files\\Inno Setup 6\\ISCC.exe"; do
    if [[ -f "$p" ]]; then iscc="$p"; break; fi
  done
  if [[ -z "$iscc" ]] && command -v iscc >/dev/null 2>&1; then iscc="$(command -v iscc)"; fi
  if [[ -z "$iscc" ]] && command -v ISCC.exe >/dev/null 2>&1; then iscc="$(command -v ISCC.exe)"; fi
  if [[ -z "$iscc" ]]; then
    echo "error: 缺 Inno Setup 6（ISCC.exe）——安装器唯一产物链，不做 SFX/NSIS 静默降级" >&2
    return 1
  fi
  echo "== Inno Setup: $iscc"
  iss_file="$(mktemp --suffix=.iss 2>/dev/null || mktemp -t iss).iss"
  # icon 若存在转 ico（若缺则跳过）
  local icon_line=""
  if [[ -f "$ROOT/assets/icon.png" ]]; then
    if command -v magick >/dev/null 2>&1 && [[ ! -f "$ROOT/assets/icon.ico" ]]; then
      magick "$ROOT/assets/icon.png" -define icon:auto-resize=16,32,48,64,128,256 "$ROOT/assets/icon.ico" 2>/dev/null || true
    fi
    if [[ -f "$ROOT/assets/icon.ico" ]]; then
      local icon_win
      if command -v cygpath >/dev/null 2>&1; then icon_win="$(cygpath -w "$ROOT/assets/icon.ico")"; else icon_win="$ROOT/assets/icon.ico"; fi
      icon_line="SetupIconFile=$icon_win"
    fi
  fi
  # 语言包条件化：runner 的 Inno 安装形态不定，缺 ChineseSimplified.isl 时编译
  # 不得因此失败（英文兜底）
  local lang_line='Name: "chinese"; MessagesFile: "compiler:Languages\\ChineseSimplified.isl"'
  local iscc_dir="$(dirname "$iscc")"
  if [[ ! -f "$iscc_dir/Languages/ChineseSimplified.isl" ]]; then
    lang_line=""
    echo "   warn: runner 缺 ChineseSimplified.isl，安装器仅英文界面"
  fi
  cat > "$iss_file" <<ISS_EOF
[Setup]
AppName=DeepSeek Harness Desktop
AppVersion=$VERSION
AppPublisher=ZK-Andy
AppPublisherURL=https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop
DefaultDirName={autopf}\\DeepSeek Harness Desktop
DefaultGroupName=DeepSeek Harness Desktop
OutputDir=$out_dir
OutputBaseFilename=$iss_base
Compression=lzma
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
$icon_line
UninstallDisplayIcon={app}\\DeepSeek.Harness.Desktop.exe
DisableProgramGroupPage=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
$lang_line

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "$staging_win\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\\DeepSeek Harness Desktop"; Filename: "{app}\\DeepSeek.Harness.Desktop.exe"
Name: "{group}\\{cm:UninstallProgram,DeepSeek Harness Desktop}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\\DeepSeek Harness Desktop"; Filename: "{app}\\DeepSeek.Harness.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\\DeepSeek.Harness.Desktop.exe"; Description: "{cm:LaunchProgram,DeepSeek Harness Desktop}"; Flags: nowait postinstall skipifsilent
ISS_EOF
  if command -v cygpath >/dev/null 2>&1; then iss_file_win="$(cygpath -w "$iss_file")"; else iss_file_win="$iss_file"; fi
  # MSYS2_ARG_CONV_EXCL：防 /Q 被当 POSIX 路径转换（本轮实锤的恒失败根因）
  local compile_ok=0
  if MSYS2_ARG_CONV_EXCL='*' "$iscc" /Q "$iss_file_win" 2>&1 | tail -30; then
    [[ -f "$installer" ]] && compile_ok=1
  fi
  rm -f "$iss_file"
  if [[ $compile_ok -ne 1 ]]; then
    echo "error: Inno Setup 编译失败（见上方输出）——安装器唯一产物链，不做静默降级" >&2
    return 1
  fi
  echo "== 产物 installer exe (Inno Setup): $installer ($(du -h "$installer" 2>/dev/null | cut -f1 || ls -lh "$installer" | awk '{print $5}'))"
  return 0
}

if create_installer_exe "$STAGE" "$INSTALLER"; then
  echo "== Windows 安装器已生成 = "
  ls -lh "$INSTALLER" 2>&1 | head -5 || true
  # 自签安装器 exe（仅内部/开发；同 SELF_SIGN=1 门控）
  if [[ "${SELF_SIGN:-0}" == "1" ]]; then sign_windows "$INSTALLER"; fi
else
  echo "error: 未能生成安装器 exe（Inno Setup 编译失败；不做静默降级）" >&2
  exit 1
fi

# 最终产物清单
echo "== 最终产物清单："
ls -lh "$OUT"/DeepSeek.Harness.Desktop* 2>&1 | head -20 || true
