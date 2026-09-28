#!/usr/bin/env bash
# package-windows.sh — 从 .NET publish 输出打 Windows 安装器（exe，Inno Setup 唯一）。
# Inno 编译失败必须 fail loud——绝不静默降级（历史教训：Git Bash 把 iscc 的 /Q 当
# POSIX 路径转换成第二个脚本文件名致编译恒失败，NSIS/SFX 回退链把失败吞成
# 「成功」，Windows 发布资产实际长期为 7z 自解压包而非安装器，2026-08-29 冒烟实锤）。
# online-first（ADR online-first-unbundled-runtime）：包只带壳 + 安装器自带插件资源
# （resources/plugins/dsh-desktop-companion.tgz）；运行时 = 用户 PATH 全局 dsh，不再捆绑闭包。
# 布局：publish 全量 + resources/plugins
# Inno 脚本模板（82 行）唯一家 = packaging/windows/installer.iss.in，本脚本只渲染占位。
# 用法：
#   scripts/package-windows.sh [publish_dir]
#   scripts/package-windows.sh --stage-only [dir]
#   scripts/package-windows.sh --self-test     # 离线夹具（共用头部 + 模板渲染）
# 环境：VERSION、ARCH（x64/arm64，现仅 x64 有完整测试）、SELF_SIGN=1（自签，仅内部/开发——
#       实现见 scripts/dev-sign.sh）
set -euo pipefail

ROOT_SCRIPTS="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/packaging-common.sh
source "$ROOT_SCRIPTS/lib/packaging-common.sh"

ISS_TEMPLATE="$ROOT_SCRIPTS/../packaging/windows/installer.iss.in"

# 渲染 Inno 模板：逐行参数展开替换（非 sed——Windows 路径含 `\`，sed 替换串要转义，
# 参数展开是字面替换，无此坑）。读调用方变量：iss_app_id / iss_out_dir / iss_base /
# iss_license / iss_icon_line / iss_lang_line / iss_staging_win 与 VERSION。
# 模板占位（packaging/windows/installer.iss.in 里 `@名字@`，本处即清单唯一家）：
#   @APP_ID@ 稳定 AppId GUID（改则断升级链） | @APP_VERSION@ 包版本
#   @OUT_DIR@ 输出目录（Windows 形态） | @ISS_BASE@ 输出文件名（无扩展名）
#   @LICENSE_WIN@ 许可证路径（Windows 形态） | @ICON_LINE@ SetupIconFile 行
#   @LANG_LINE@ 中文语言包行（缺 isl 时为空串） | @STAGING_WIN@ staging 内容根（Windows 形态）
render_iss_template() { # $1=模板 $2=输出 .iss
  local tpl="$1" out="$2" line
  [[ -f "$tpl" ]] || die "Inno 模板缺失: $tpl（packaging/windows/installer.iss.in）"
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line//@APP_ID@/$iss_app_id}"
    line="${line//@APP_VERSION@/$VERSION}"
    line="${line//@OUT_DIR@/$iss_out_dir}"
    line="${line//@ISS_BASE@/$iss_base}"
    line="${line//@LICENSE_WIN@/$iss_license}"
    line="${line//@ICON_LINE@/$iss_icon_line}"
    line="${line//@LANG_LINE@/$iss_lang_line}"
    line="${line//@STAGING_WIN@/$iss_staging_win}"
    printf '%s\n' "$line"
  done <"$tpl" >"$out"
  # 残留占位 = 模板新增了占位而渲染没接线；Inno 只会报一个无关的语法错，此处先点名。
  if grep -qE '@[A-Z_]+@' "$out"; then
    grep -nE '@[A-Z_]+@' "$out" >&2 || true
    die "Inno 模板渲染后仍有未替换占位（见上）"
  fi
}

# iscc 位置：2 条标准路径 + PATH（原六路级联收敛）。
find_iscc() {
  local p
  for p in "/c/Program Files (x86)/Inno Setup 6/ISCC.exe" "/c/Program Files/Inno Setup 6/ISCC.exe"; do
    if [[ -f "$p" ]]; then
      echo "$p"
      return 0
    fi
  done
  if command -v iscc >/dev/null 2>&1; then
    command -v iscc
    return 0
  fi
  if command -v ISCC.exe >/dev/null 2>&1; then
    command -v ISCC.exe
    return 0
  fi
  return 1
}

iss_self_test() { # 模板渲染离线夹具（无需 Windows / ISCC）：占位全替换 + 关键行形状
  local tmp fails=0
  tmp="$(mktemp -d)"
  iss_app_id='{{4d5c1f64-5ad2-5028-9790-58da43a81685}}'
  iss_out_dir='C:\out dir'
  iss_base='DeepSeek.Harness.Desktop_1.2.3_windows-x64-setup'
  iss_license='C:\repo\LICENSE'
  iss_icon_line='SetupIconFile=C:\repo\assets\icon.ico'
  iss_lang_line='Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"'
  iss_staging_win='C:\repo\artifacts\stage'
  VERSION='1.2.3'
  render_iss_template "$ISS_TEMPLATE" "$tmp/out.iss"
  _iss_assert() { # $1=描述 $2=固定串
    if grep -qF "$2" "$tmp/out.iss"; then
      echo "ok: $1"
    else
      echo "FAIL: $1（未找到: $2）" >&2
      fails=$((fails + 1))
    fi
  }
  _iss_assert "AppId 替换" 'AppId={{4d5c1f64-5ad2-5028-9790-58da43a81685}}'
  _iss_assert "版本替换" 'AppVersion=1.2.3'
  _iss_assert "输出目录替换（含空格）" 'OutputDir=C:\out dir'
  _iss_assert "输出名替换" 'OutputBaseFilename=DeepSeek.Harness.Desktop_1.2.3_windows-x64-setup'
  _iss_assert "许可证路径替换" 'LicenseFile=C:\repo\LICENSE'
  _iss_assert "图标行替换" 'SetupIconFile=C:\repo\assets\icon.ico'
  _iss_assert "语言行替换" 'ChineseSimplified.isl'
  _iss_assert "staging 源替换" 'Source: "C:\repo\artifacts\stage\*"'
  # 空语言行（runner 缺 ChineseSimplified.isl）= 渲染成空行，非残留占位
  iss_lang_line=''
  render_iss_template "$ISS_TEMPLATE" "$tmp/out2.iss"
  if grep -qE '^Name: "chinese"' "$tmp/out2.iss"; then
    echo "FAIL: 空语言行仍渲染出 chinese 语言项" >&2
    fails=$((fails + 1))
  else
    echo "ok: 空语言行渲染为空"
  fi
  rm -rf "$tmp"
  if [[ $fails -eq 0 ]]; then
    echo "iss self-test: PASS"
    return 0
  fi
  echo "iss self-test: FAIL（$fails 项）" >&2
  return 1
}

if [[ "${1:-}" == "--self-test" ]]; then
  packaging_self_test
  iss_self_test
  exit $?
fi

packaging_init windows "$@"
STAGE="$OUT/stage/DeepSeek.Harness.Desktop"

echo "== 组装 staging: $STAGE"
rm -rf "$STAGE" && mkdir -p "$STAGE"
cp -r "$PUBLISH_DIR/." "$STAGE/"

# 安装器自带插件资源：companion tgz 从仓库源码现打并校验（fail loud）。
# 不再捆绑运行时闭包——首启引导确保全局 dsh（ADR simple-shell-single-global-dsh）。
mkdir -p "$STAGE/resources/plugins"
bash "$ROOT/scripts/build-companion-tgz.sh" "$STAGE/resources/plugins/dsh-desktop-companion.tgz"

# 布局断言（staging 是 Inno [Files] 的唯一内容源，断言 staging 即断言安装器）：
# 闭包残留 / 插件 tgz 名称体积 / 主 exe + 托管程序集 + 原生库 + runtimes + wwwroot，
# 缺一 fail loud。判据唯一家在 scripts/verify-package-layout.sh。
packaging_assert_layout windows "$STAGE"

echo "== staging 体积: $(du -sh "$STAGE" | cut -f1)"
if [[ $STAGE_ONLY -eq 1 ]]; then
  find "$STAGE" -maxdepth 2 -type d | sort | head -20 || true
  ls -lh "$STAGE/resources/plugins/" 2>&1 | head -3 || true
  exit 0
fi

mkdir -p "$OUT"
# 自签（可选，仅内部/开发验证用）——显式 SELF_SIGN=1 才启用，不默认打扰发布；
# 用 CurrentUser\My 里的自签代码签名证书（缺则自动建）+ signtool 签 Authenticode。
# 注意：自签证书不被终端用户信任，不消除 SmartScreen「未知发布者」告警，仅治本机/内部。
# 实现唯一家在 scripts/dev-sign.sh。
if [[ "${SELF_SIGN:-0}" == "1" && -f "$STAGE/DeepSeek.Harness.Desktop.exe" ]]; then
  bash "$ROOT_SCRIPTS/dev-sign.sh" windows "$STAGE/DeepSeek.Harness.Desktop.exe"
fi

# 单一安装器产物（不再单独产出便携 zip）。命名含 windows 标识，避免与 macOS/dmg 同名冲突。
INSTALLER="$OUT/DeepSeek.Harness.Desktop_${VERSION}_windows-${ARCH}-setup.exe"
rm -f "$INSTALLER"

# 稳定 AppId（对标 Ryn BundleCommand 确定性 UpgradeCode + MajorUpgrade 语义）：
# Inno 靠 AppId 识别同一产品的升级覆盖，无它则重装并存/残留。此 GUID 由
# bundle identifier 派生一次后写死，永不再变（改则断升级链）。
iss_app_id="{{4d5c1f64-5ad2-5028-9790-58da43a81685}}"

# Windows 形态转换（Git Bash 下 cygpath；非 MINGW 环境原样），iss 里不能出现 POSIX 路径。
win_path() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}
iss_staging_win="$(win_path "$STAGE")"
iss_out_dir="$(win_path "$(dirname "$INSTALLER")")"
iss_base="$(basename "$INSTALLER" .exe)"

# 许可证随包（对标 hairyf bundle.licenseFile）：仓库根 LICENSE（MIT）。
[[ -f "$ROOT/LICENSE" ]] || die "缺许可证文件 LICENSE（安装器须随包）"
iss_license="$(win_path "$ROOT/LICENSE")"

# icon：提交物优先（assets/icon.ico），缺则由 icon.png 现转、转不出即 fail loud。
# 对标 Ryn Assets/ryn-icon.ico + 默认图标兜底：静默无图标安装器不可接受。
if [[ -f "$ROOT/assets/icon.ico" ]]; then
  iss_icon_line="SetupIconFile=$(win_path "$ROOT/assets/icon.ico")"
elif [[ -f "$ROOT/assets/icon.png" ]]; then
  command -v magick >/dev/null 2>&1 || die "缺 assets/icon.ico 且无 magick 可由 icon.png 现转（安装器图标不可静默缺失）"
  magick "$ROOT/assets/icon.png" -define icon:auto-resize=16,32,48,64,128,256 "$ROOT/assets/icon.ico" \
    || die "icon.png 转 ico 失败"
  iss_icon_line="SetupIconFile=$(win_path "$ROOT/assets/icon.ico")"
else
  die "缺安装器图标（assets/icon.ico 与 assets/icon.png 均不存在）"
fi

# 语言包条件化：runner 的 Inno 安装形态不定，缺 ChineseSimplified.isl 时编译
# 不得因此失败（英文兜底）
iss_lang_line='Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"'
iscc="$(find_iscc)" || die "缺 Inno Setup 6（ISCC.exe）——安装器唯一产物链，不做 SFX/NSIS 静默降级"
if command -v cygpath >/dev/null 2>&1 && [[ -n "$iscc" ]]; then
  if [[ ! -f "$(dirname "$iscc")/Languages/ChineseSimplified.isl" ]]; then
    iss_lang_line=""
    warn "runner 缺 ChineseSimplified.isl，安装器仅英文界面"
  fi
fi

echo "== Inno Setup: $iscc"
iss_file="$(mktemp --suffix=.iss 2>/dev/null || mktemp -t iss).iss"
render_iss_template "$ISS_TEMPLATE" "$iss_file"
iss_file_win="$(win_path "$iss_file")"
# MSYS2_ARG_CONV_EXCL：防 /Q 被当 POSIX 路径转换（本轮实锤的恒失败根因）
compile_ok=0
if MSYS2_ARG_CONV_EXCL='*' "$iscc" /Q "$iss_file_win" 2>&1 | tail -30; then
  [[ -f "$INSTALLER" ]] && compile_ok=1
fi
rm -f "$iss_file"
[[ $compile_ok -eq 1 ]] || die "Inno Setup 编译失败（见上方输出）——安装器唯一产物链，不做静默降级"

echo "== 产物 installer exe (Inno Setup): $INSTALLER ($(du -h "$INSTALLER" 2>/dev/null | cut -f1 || echo '?'))"
ls -lh "$INSTALLER" 2>&1 | head -5 || true
# 自签安装器 exe（仅内部/开发；同 SELF_SIGN=1 门控）
if [[ "${SELF_SIGN:-0}" == "1" ]]; then
  bash "$ROOT_SCRIPTS/dev-sign.sh" windows "$INSTALLER"
fi

# 最终产物清单
echo "== 最终产物清单："
ls -lh "$OUT"/DeepSeek.Harness.Desktop* 2>&1 | head -20 || true
