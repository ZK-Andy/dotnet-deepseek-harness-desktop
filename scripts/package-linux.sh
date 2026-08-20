#!/usr/bin/env bash
# package-linux.sh — 从 .NET publish 输出打 Linux 安装包（deb + rpm）。
# 参照 pilot-harness apps/desktop/electron-builder.yml 的 Linux 产物理念：
#   asar: false（DSH 闭包含 symlink/跨平台 prebuild，原样保留，不打包为单文件）
#   linux.desktop（Name/Comment/Categories/StartupWMClass）
#   产物为 AppImage→deb/rpm（此处为 .NET 自包含 publish + resources/runtime 手工组装的等价物）
# 布局：
#   usr/lib/<app>/   = dotnet publish 全量 + resources/runtime（RuntimeLocator 按此探测）
#   usr/bin/<app>    = 符号链接
#   usr/share/applications/<app>.desktop（对齐 pilot-harness linux.desktop）
# 用法：
#   scripts/package-linux.sh [publish_dir]          # 全量（需 dpkg-deb + rpmbuild；Ubuntu runner 自带 dpkg-deb，rpm 需 apt 安装）
#   scripts/package-linux.sh --stage-only [dir]     # 仅组装 staging，供无工具机校验布局与 RuntimeLocator
# 环境：VERSION（默认 0.1.0，CI 由 tag/inputs.version 注入）、MAINTAINER、ARCH
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ARG1="${1:-}"
STAGE_ONLY=0
if [[ "$ARG1" == "--stage-only" ]]; then STAGE_ONLY=1; ARG1="${2:-}"; fi

PUBLISH_DIR="${ARG1:-$ROOT/artifacts/publish-linux-x64}"
VERSION="${VERSION:-0.1.0}"
ARCH="${ARCH:-amd64}"
APP="deepseek-harness-desktop"
MAINTAINER="${MAINTAINER:-zhangkun <253117546@qq.com>}"
OUT="$ROOT/artifacts/linux-x64"
STAGE="$OUT/stage/$APP-$VERSION"

[[ -d "$PUBLISH_DIR" ]] || { echo "error: publish 目录不存在: $PUBLISH_DIR" >&2; exit 1; }

echo "== 组装 staging: $STAGE"
DEST="usr/lib/$APP"
rm -rf "$STAGE" && mkdir -p "$STAGE/$DEST"

# 1) publish 全量（dotnet 自包含，含 saucer/lib* 等原生依赖）
cp -r "$PUBLISH_DIR/." "$STAGE/$DEST/"

# 2) resources/runtime 并入包 —— 必须保持 resources/runtime/ 嵌套（RuntimeLocator.ResolveRuntimeDirectory）
# 参照 pilot-harness：闭包含数万文件 + 跨平台 prebuild/相对 symlink，原样收入，不走 asar/压缩重打包。
if [[ -d "$ROOT/resources/runtime" ]]; then
  echo "   并入 resources/runtime（pilot-harness 整树方案）"
  mkdir -p "$STAGE/$DEST/resources"
  cp -a "$ROOT/resources/runtime" "$STAGE/$DEST/resources/"
  # 校验 Locator 能命中（fail loud，免装后才发现捆绑运行时失效）
  if [[ ! -f "$STAGE/$DEST/resources/runtime/node" || ! -f "$STAGE/$DEST/resources/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js" ]]; then
    echo "error: staging 的 resources/runtime 不符合 RuntimeLocator 预期" >&2
    echo "  期望：resources/runtime/node + resources/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js" >&2
    ls -R "$STAGE/$DEST/resources/runtime" 2>&1 | head -60
    exit 1
  fi
else
  echo "warn: 未找到 $ROOT/resources/runtime，包将回退 PATH dsh（安装环境通常无 dsh，导致启动失败）" >&2
fi
chmod +x "$STAGE/$DEST/DeepSeek.Harness.Desktop"

# 3) bin 符号链接 + desktop 入口（对齐 pilot-harness linux.desktop）
mkdir -p "$STAGE/usr/bin" "$STAGE/usr/share/applications"
ln -s "/$DEST/DeepSeek.Harness.Desktop" "$STAGE/usr/bin/$APP"
cat > "$STAGE/usr/share/applications/$APP.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=DeepSeek Harness Desktop
Comment=Desktop client for DeepSeek Harness
Comment[zh_CN]=DeepSeek Harness 桌面客户端
Exec=$APP
Icon=$APP
Terminal=false
Categories=Development;IDE;Utility;
StartupWMClass=DeepSeek.Harness.Desktop
EOF

# 3b) 图标（对齐 pilot-harness assets/icon.png → hicolor 512 + brand-icon）
if [[ -d "$ROOT/assets/icons" ]]; then
  echo "   安装图标（hicolor）"
  for sz in 16 32 48 64 128 256 512 1024; do
    if [[ -f "$ROOT/assets/icons/${sz}x${sz}/apps.png" ]]; then
      mkdir -p "$STAGE/usr/share/icons/hicolor/${sz}x${sz}/apps"
      cp "$ROOT/assets/icons/${sz}x${sz}/apps.png" "$STAGE/usr/share/icons/hicolor/${sz}x${sz}/apps/$APP.png"
    fi
  done
  mkdir -p "$STAGE/usr/share/pixmaps"
  cp "$ROOT/assets/icon.png" "$STAGE/usr/share/pixmaps/$APP.png"
fi

echo "== staging 体积: $(du -sh "$STAGE" | cut -f1)"
if [[ $STAGE_ONLY -eq 1 ]]; then
  echo "(--stage-only 校验布局)："
  find "$STAGE" -maxdepth 3 -type d | sort | head -30
  echo "--- 入口 ---"
  ls -lh "$STAGE/$DEST/resources/runtime/node" 2>&1 | head -1 || echo "node 缺失"
  ls -lh "$STAGE/$DEST/resources/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js" 2>&1 | head -1 || echo "bin.js 缺失"
  exit 0
fi

command -v dpkg-deb >/dev/null || { echo "error: 缺 dpkg-deb（Ubuntu runner 自带；本地可用 --stage-only 校验）" >&2; exit 1; }
command -v rpmbuild >/dev/null || { echo "error: 缺 rpmbuild（Ubuntu: sudo apt-get install -y rpm）" >&2; exit 1; }

echo "== [deb]"
mkdir -p "$STAGE/DEBIAN"
cat > "$STAGE/DEBIAN/control" <<EOF
Package: $APP
Version: $VERSION
Section: devel
Priority: optional
Architecture: $ARCH
Maintainer: $MAINTAINER
Depends: libwebkitgtk-6.0-4
Description: DeepSeek Harness Desktop for .NET (native shell + bundled runtime)
EOF
mkdir -p "$OUT"
dpkg-deb --root-owner-group --build "$STAGE" "$OUT/${APP}_${VERSION}_${ARCH}.deb"

echo "== [rpm]"
SPEC="$OUT/$APP.spec"
cat > "$SPEC" <<EOF
Name: $APP
Version: ${VERSION%%-*}
Release: 1%{?dist}
Summary: DeepSeek Harness Desktop for .NET
License: MIT
URL: https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop
Packager: $MAINTAINER
BuildArch: ${ARCH/amd64/x86_64}
# 参照 pilot-harness asar:false 与数万文件闭包：rpm 自动依赖扫描会把 node_modules 跨平台 prebuild
#（aarch64/musl/ld-linux/perl 等）误判为运行依赖，导致 dnf 安装失败 → 整体禁用自动依赖，显式声明真实依赖。
AutoReqProv: no
# saucer 动态链接 libwebkitgtk-6.0.so.4（WebKitGTK 6 / GTK4），由该包连带拉 GTK/JavaScriptCore/soup/cairo 等
Requires: libwebkitgtk-6.0.so.4
# 跨平台 .node 预编译体会被 rpm 的 brp-strip 与 debuginfo 抽取误伤（如 linux-arm64/pty.node）→ 整体禁用
%global _enable_debug_packages 0
%define __os_install_post %{nil}
%description
Desktop client for DeepSeek Harness (Ryn native webview shell + bundled runtime, pilot-harness packaging model).

%install
cp -r "$STAGE/usr" %{buildroot}/

%files
/usr/lib/$APP
/usr/bin/$APP
/usr/share/applications/$APP.desktop
/usr/share/icons
/usr/share/pixmaps
EOF
rpmbuild --define "_topdir $OUT/rpmbuild" --define "_specdir $OUT" -bb "$SPEC"
# 重命名去掉 Release 后缀（用户期望 deepseek-harness-desktop-0.1.5.x86_64.rpm 而非 -1）
for f in "$OUT/rpmbuild/RPMS"/*/*.rpm; do
  [[ -f "$f" ]] || continue
  if [[ "$f" == *"-1."* ]]; then
    mv "$f" "${f/-1./.}"
  fi
done

echo "== 产物:"
ls -lh "$OUT"/*.deb "$OUT"/rpmbuild/RPMS/**/*.rpm 2>&1 | grep -E "^-|deepseek" || true
echo "== deb 校验（如可用）:"
dpkg-deb -I "$OUT/${APP}_${VERSION}_${ARCH}.deb" 2>&1 | head -20 || true
echo "== rpm 校验（如可用）:"
rpm -qp --requires "$OUT/rpmbuild/RPMS"/*/*.rpm 2>&1 | head -30 || true
