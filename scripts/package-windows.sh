#!/usr/bin/env bash
# package-windows.sh — 从 .NET publish 输出打 Windows 包（zip）。
# 布局：publish 全量 + resources/runtime
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

if [[ -d "$ROOT/resources/runtime" ]]; then
  echo "   并入 resources/runtime"
  mkdir -p "$STAGE/resources"
  if [[ "$OSTYPE" == msys* || "$OSTYPE" == cygwin* || -n "${WINDIR:-}" ]]; then
    # Windows: 解引用拷贝，避免 junction/symlink 失败
    cp -Lr "$ROOT/resources/runtime" "$STAGE/resources/" 2>/dev/null || powershell -Command "Copy-Item -Path '$ROOT/resources/runtime' -Destination '$STAGE/resources' -Recurse -Force" 2>/dev/null || cp -r "$ROOT/resources/runtime" "$STAGE/resources/"
  else
    cp -a "$ROOT/resources/runtime" "$STAGE/resources/"
  fi
  if [[ ! -f "$STAGE/resources/runtime/node.exe" && ! -f "$STAGE/resources/runtime/node" ]]; then
    echo "warn: staging 缺 node(.exe)，可能架构不匹配 ($RID)" >&2
  fi
  if [[ ! -f "$STAGE/resources/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js" ]]; then
    echo "warn: stagng 缺 dsh 入口" >&2
  fi
  if [[ -f "$STAGE/resources/runtime/dshmarket.tgz" ]]; then
    SZ=$(stat -c%s "$STAGE/resources/runtime/dshmarket.tgz" 2>/dev/null || stat -f%z "$STAGE/resources/runtime/dshmarket.tgz" 2>/dev/null || echo 0)
    if [[ "$SZ" -lt 10240 ]]; then echo "error: dshmarket.tgz 过小" >&2; exit 1; fi
  fi
fi

echo "== staging 体积: $(du -sh "$STAGE" | cut -f1)"
if [[ $STAGE_ONLY -eq 1 ]]; then
  find "$STAGE" -maxdepth 2 -type d | sort | head -20 || true
  ls -lh "$STAGE/resources/runtime/node"* 2>&1 | head -5 || echo "node 缺失"
  exit 0
fi

mkdir -p "$OUT"
# 产物命名加入 windows 标识，避免与 macOS 同名冲突（原 _x64.zip 无平台前缀）
ZIP="$OUT/DeepSeek.Harness.Desktop_${VERSION}_windows-${ARCH}.zip"
INSTALLER="$OUT/DeepSeek.Harness.Desktop_${VERSION}_windows-${ARCH}-setup.exe"
rm -f "$ZIP" "$INSTALLER"

# 兜底压缩：zip → 7z → tar (bsdtar -a) → powershell (cygpath 转换)
create_zip() {
  local src="$1" dst="$2"
  local src_dir src_base
  src_dir="$(dirname "$src")"
  src_base="$(basename "$src")"
  if command -v zip >/dev/null 2>&1; then
    echo "   尝试 zip"
    (cd "$src_dir" && zip -r -q "$dst" "$src_base") && return 0
    echo "   zip 失败，回退"
  fi
  if command -v 7z >/dev/null 2>&1; then
    echo "   尝试 7z"
    (cd "$src_dir" && 7z a -tzip "$dst" "$src_base" -mx=0 >/dev/null 2>&1) && return 0
    7z a -tzip "$dst" "$src" -mx=0 >/dev/null 2>&1 && return 0
    echo "   7z 失败，回退"
  fi
  if command -v tar >/dev/null 2>&1; then
    echo "   尝试 tar -a (bsdtar)"
    (cd "$src_dir" && tar -a -c -f "$dst" "$src_base" 2>/dev/null) && return 0
    (cd "$src_dir" && tar -c -f "$dst" "$src_base" 2>/dev/null) && return 0
    echo "   tar 失败，回退"
  fi
  local ps=""
  if command -v pwsh >/dev/null 2>&1; then ps="pwsh"
  elif command -v powershell >/dev/null 2>&1; then ps="powershell"
  fi
  if [[ -n "$ps" ]]; then
    echo "   尝试 $ps Compress-Archive"
    local src_win dst_win
    if command -v cygpath >/dev/null 2>&1; then
      src_win="$(cygpath -w "$src" 2>/dev/null || echo "$src")"
      dst_win="$(cygpath -w "$dst" 2>/dev/null || echo "$dst")"
    else
      # 回退：/d/a -> D:\a (仅处理常见盘符)
      src_win="$(echo "$src" | sed -E 's#^/([cCdD])/#\1:/#' | tr '/' '\\')"
      # 上行在 BSD sed 可能失败，二次回退
      if [[ "$src_win" == "$src" ]]; then
        src_win="$(echo "$src" | sed 's#^/d/#D:\\#; s#^/c/#C:\\#; s#/#\\#g')"
        dst_win="$(echo "$dst" | sed 's#^/d/#D:\\#; s#^/c/#C:\\#; s#/#\\#g')"
      else
        dst_win="$(echo "$dst" | sed -E 's#^/([cCdD])/#\1:/#' | tr '/' '\\')"
      fi
      # 修正首字符盘符大写
      src_win="$(echo "$src_win" | sed 's#^c:#C:#; s#^d:#D:#')"
      dst_win="$(echo "$dst_win" | sed 's#^c:#C:#; s#^d:#D:#')"
    fi
    echo "   ps src=$src_win dst=$dst_win"
    "$ps" -NoProfile -Command "Compress-Archive -Path '$src_win' -DestinationPath '$dst_win' -Force" 2>&1 | head -20 || true
    if [[ -f "$dst" ]]; then return 0; fi
    "$ps" -NoProfile -Command "Compress-Archive -Path '$src_win\\*' -DestinationPath '$dst_win' -Force" 2>&1 | head -20 || true
    if [[ -f "$dst" ]]; then return 0; fi
  fi
  return 1
}

if ! create_zip "$STAGE" "$ZIP"; then
  echo "error: 无法创建 zip（zip/7z/tar/powershell 均失败）" >&2; exit 1
fi
echo "== 产物 zip: $ZIP ($(du -h "$ZIP" 2>/dev/null | cut -f1 || ls -lh "$ZIP" | awk '{print $5}'))"
if command -v unzip >/dev/null 2>&1; then
  unzip -l "$ZIP" 2>&1 | head -20 || true
elif command -v 7z >/dev/null 2>&1; then
  7z l "$ZIP" 2>&1 | head -40 || true
elif command -v tar >/dev/null 2>&1; then
  tar -tf "$ZIP" 2>&1 | head -20 || true
else
  local ps2="powershell"; command -v pwsh >/dev/null 2>&1 && ps2="pwsh"
  if command -v cygpath >/dev/null 2>&1; then
    ZIP_WIN="$(cygpath -w "$ZIP" 2>/dev/null || echo "$ZIP")"
  else
    ZIP_WIN="$ZIP"
  fi
  "$ps2" -NoProfile -Command "Get-ChildItem '$ZIP_WIN' | Format-List; try { (Get-ChildItem '$ZIP_WIN').Length } catch {}" 2>/dev/null | head -20 || true
  ls -lh "$ZIP" 2>&1 | head -5 || true
fi

# 额外产出安装器 exe（Windows 期待 exe 安装器；Linux 已有 deb/rpm，macOS 已有 zip+dmg）
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
else
  echo "warn: 未能生成安装器 exe（无 Inno Setup/NSIS/7z SFX），仅保留 zip" >&2
  echo "   Windows 仍可通过 zip 便携运行（解压后运行 DeepSeek.Harness.Desktop.exe）" >&2
  rm -f "$INSTALLER"
fi

# 最终产物清单
echo "== 最终产物清单："
ls -lh "$OUT"/DeepSeek.Harness.Desktop* 2>&1 | head -20 || true
