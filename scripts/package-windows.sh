#!/usr/bin/env bash
# package-windows.sh — 从 .NET publish 输出打 Windows 安装器（exe，Inno Setup/NSIS/7z SFX）。
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
# 优先 Inno Setup (iscc)，回退 NSIS (makensis)，再回退 7z SFX
create_installer_exe() {
  local staging="$1" installer="$2"
  local staging_win installer_win out_dir iss_file
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
  local iss_out_dir="$out_dir"
  local iss_base="$(basename "$installer" .exe)"
  # 优先 Inno Setup 6
  local iscc=""
  for p in "/c/Program Files (x86)/Inno Setup 6/ISCC.exe" "/c/Program Files/Inno Setup 6/ISCC.exe" "C:\\Program Files (x86)\\Inno Setup 6\\ISCC.exe" "C:\\Program Files\\Inno Setup 6\\ISCC.exe"; do
    if [[ -f "$p" ]]; then iscc="$p"; break; fi
  done
  if [[ -z "$iscc" ]] && command -v iscc >/dev/null 2>&1; then iscc="$(command -v iscc)"; fi
  if [[ -z "$iscc" ]] && command -v ISCC.exe >/dev/null 2>&1; then iscc="$(command -v ISCC.exe)"; fi
  if [[ -n "$iscc" ]]; then
    echo "   尝试 Inno Setup: $iscc"
    iss_file="$(mktemp --suffix=.iss 2>/dev/null || mktemp -t iss).iss"
    # icon 若存在转 ico（若缺则跳过）
    local icon_line=""
    if [[ -f "$ROOT/assets/icon.png" ]]; then
      # 尝试在 Windows 上用 magick 转 ico（若可用），否则忽略
      if command -v magick >/dev/null 2>&1 && [[ ! -f "$ROOT/assets/icon.ico" ]]; then
        magick "$ROOT/assets/icon.png" -define icon:auto-resize=16,32,48,64,128,256 "$ROOT/assets/icon.ico" 2>/dev/null || true
      fi
      if [[ -f "$ROOT/assets/icon.ico" ]]; then
        local icon_win
        if command -v cygpath >/dev/null 2>&1; then icon_win="$(cygpath -w "$ROOT/assets/icon.ico")"; else icon_win="$ROOT/assets/icon.ico"; fi
        icon_line="SetupIconFile=$icon_win"
      fi
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
Name: "chinese"; MessagesFile: "compiler:Languages\\ChineseSimplified.isl"

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
    echo "   ISS: $iss_file"
    cat "$iss_file" 2>&1 | head -40 || true
    if "$iscc" /Q "$iss_file" 2>&1 | tail -30; then
      if [[ -f "$installer" ]]; then
        echo "== 产物 installer exe (Inno Setup): $installer ($(du -h "$installer" 2>/dev/null | cut -f1 || ls -lh "$installer" | awk '{print $5}'))"
        rm -f "$iss_file"
        return 0
      fi
    fi
    echo "   Inno Setup 失败，回退" >&2
    rm -f "$iss_file"
  fi
  # 回退 NSIS
  local makensis=""
  for p in "/c/Program Files (x86)/NSIS/makensis.exe" "/c/Program Files/NSIS/makensis.exe" "C:\\Program Files (x86)\\NSIS\\makensis.exe" "C:\\Program Files\\NSIS\\makensis.exe"; do
    if [[ -f "$p" ]]; then makensis="$p"; break; fi
  done
  if [[ -z "$makensis" ]] && command -v makensis >/dev/null 2>&1; then makensis="$(command -v makensis)"; fi
  if [[ -n "$makensis" ]]; then
    echo "   尝试 NSIS: $makensis"
    local nsi_file
    nsi_file="$(mktemp --suffix=.nsi 2>/dev/null || mktemp -t nsi).nsi"
    cat > "$nsi_file" <<NSIS_EOF
!include "MUI2.nsh"
Name "DeepSeek Harness Desktop"
OutFile "$installer_win"
InstallDir "\$PROGRAMFILES64\\DeepSeek Harness Desktop"
RequestExecutionLevel user
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"
Section "Install"
  SetOutPath "\$INSTDIR"
  File /r "$staging_win\\*.*"
  CreateDirectory "\$SMPROGRAMS\\DeepSeek Harness Desktop"
  CreateShortCut "\$SMPROGRAMS\\DeepSeek Harness Desktop\\DeepSeek Harness Desktop.lnk" "\$INSTDIR\\DeepSeek.Harness.Desktop.exe"
  CreateShortCut "\$DESKTOP\\DeepSeek Harness Desktop.lnk" "\$INSTDIR\\DeepSeek.Harness.Desktop.exe"
  WriteUninstaller "\$INSTDIR\\Uninstall.exe"
SectionEnd
Section "Uninstall"
  Delete "\$INSTDIR\\*.*"
  RMDir /r "\$INSTDIR"
  Delete "\$SMPROGRAMS\\DeepSeek Harness Desktop\\DeepSeek Harness Desktop.lnk"
  Delete "\$DESKTOP\\DeepSeek Harness Desktop.lnk"
SectionEnd
NSIS_EOF
    cat "$nsi_file" 2>&1 | head -30 || true
    if "$makensis" "$nsi_file" 2>&1 | tail -30; then
      if [[ -f "$installer" ]]; then
        echo "== 产物 installer exe (NSIS): $installer ($(du -h "$installer" 2>/dev/null | cut -f1 || ls -lh "$installer" | awk '{print $5}'))"
        rm -f "$nsi_file"
        return 0
      fi
    fi
    echo "   NSIS 失败，回退" >&2
    rm -f "$nsi_file"
  fi
  # 回退 7z SFX（若可用，产出自解压 exe；需 7z.sfx）
  if command -v 7z >/dev/null 2>&1; then
    local sfx
    for sfx in "/c/Program Files/7-Zip/7z.sfx" "C:\\Program Files\\7-Zip\\7z.sfx" "/usr/lib/7zip/7z.sfx" "/usr/lib/p7zip/7z.sfx"; do
      if [[ -f "$sfx" ]]; then
        echo "   尝试 7z SFX: $sfx"
        # 7z SFX 需先压 7z 再拼 SFXstub + config + archive
        local tmp7z
        tmp7z="$(mktemp --suffix=.7z 2>/dev/null || mktemp -t sfx).7z"
        (cd "$(dirname "$staging")" && 7z a -t7z "$tmp7z" "$(basename "$staging")" -mx=9 >/dev/null 2>&1)
        if [[ -f "$tmp7z" ]]; then
          # 简单拼接 SFX（部分 SFX 模块支持 -sfx 选项直接产出 exe）
          if 7z a -sfx"$sfx" "$installer" "$staging" >/dev/null 2>&1; then
            if [[ -f "$installer" ]]; then echo "== 产物 SFX exe: $installer"; rm -f "$tmp7z"; return 0; fi
          fi
          cat "$sfx" "$tmp7z" > "$installer" 2>/dev/null || true
          if [[ -f "$installer" && -s "$installer" ]]; then echo "== 产物 SFX exe (拼接): $installer"; rm -f "$tmp7z"; return 0; fi
        fi
        rm -f "$tmp7z"
        break
      fi
    done
  fi
  return 1
}

if create_installer_exe "$STAGE" "$INSTALLER"; then
  echo "== Windows 安装器已生成 == "
  ls -lh "$INSTALLER" 2>&1 | head -5 || true
  if command -v iscc >/dev/null 2>&1 || [[ -f "/c/Program Files (x86)/Inno Setup 6/ISCC.exe" ]]; then
    echo "   （Inno Setup 已用）"
  fi
  # 自签安装器 exe（仅内部/开发；同 SELF_SIGN=1 门控）
  if [[ "${SELF_SIGN:-0}" == "1" ]]; then sign_windows "$INSTALLER"; fi
else
  echo "error: 未能生成安装器 exe（无 Inno Setup/NSIS/7z SFX；独立 zip 已不再产出）" >&2
  exit 1
fi

# 最终产物清单
echo "== 最终产物清单："
ls -lh "$OUT"/DeepSeek.Harness.Desktop* 2>&1 | head -20 || true
