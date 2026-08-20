#!/usr/bin/env bash
# package-linux.sh — 从 .NET publish 输出打 Linux 安装包（deb + rpm）。
#   usr/lib/<app>/   = 整个 publish 目录（含 resources/runtime、wwwroot 等）
#   usr/bin/<app>    = 可执行符号链接
#   usr/share/applications/<app>.desktop
# 用法：
#   scripts/package-linux.sh [publish_dir]          # 全量（需 dpkg-deb + rpmbuild）
#   scripts/package-linux.sh --stage-only [dir]     # 只生成 staging，供无工具机校验布局
# 环境：VERSION（默认 0.1.0）、MAINTAINER、ARCH
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

# 1) publish 全量
cp -r "$PUBLISH_DIR/." "$STAGE/$DEST/"

# 2) resources/runtime（脚本/CI 生成的捆绑运行时）并入包 —— 必须保持 resources/runtime/ 结构（RuntimeLocator 按此找）
if [[ -d "$ROOT/resources/runtime" ]]; then
  echo "   并入 resources/runtime"
  mkdir -p "$STAGE/$DEST/resources"
  cp -r "$ROOT/resources/runtime" "$STAGE/$DEST/resources/"
fi
chmod +x "$STAGE/$DEST/DeepSeek.Harness.Desktop"

# 3) bin 符号链接 + desktop 入口
mkdir -p "$STAGE/usr/bin" "$STAGE/usr/share/applications"
ln -s "/$DEST/DeepSeek.Harness.Desktop" "$STAGE/usr/bin/$APP"
cat > "$STAGE/usr/share/applications/$APP.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=DeepSeek Harness Desktop
Comment=Desktop client for DeepSeek Harness
Comment[zh_CN]=DeepSeek Harness 桌面客户端
Exec=$APP
Terminal=false
Categories=Development;IDE;Utility;
EOF

echo "== staging 体积: $(du -sh "$STAGE" | cut -f1)"
if [[ $STAGE_ONLY -eq 1 ]]; then
  echo "(--stage-only 校验布局：)"
  find "$STAGE" -maxdepth 3 -type d | sort | head -20
  exit 0
fi

command -v dpkg-deb >/dev/null || { echo "error: 缺 dpkg-deb（Ubuntu runner 自带）" >&2; exit 1; }
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
# webkit 由 saucer 链接 libwebkitgtk-6.0.so.4 → rpm 从 ELF 自动生成精确依赖（跨发行版），无需手写
# 自动依赖已禁用：整库 node_modules 的跨平台 prebuild 会生成 aarch64/musl/ld-linux/perl 等一堆无意义依赖
AutoReqProv: no
# 真实运行库：WebKitGTK6（GTK4）——其包会连带拉 GTK/JavaScriptCore/soup/cairo 等
Requires: libwebkitgtk-6.0.so.4
# node_modules 内含跨平台 prebuild，rpm 的 brp-strip/debuginfo 会误伤 → 整体禁用
%global _enable_debug_packages 0
%define __os_install_post %{nil}
%description
Desktop client for DeepSeek Harness (Ryn native webview shell + bundled runtime).

%install
cp -r "$STAGE/usr" %{buildroot}/

%files
/usr/lib/$APP
/usr/bin/$APP
/usr/share/applications/$APP.desktop
EOF
rpmbuild --define "_topdir $OUT/rpmbuild" --define "_specdir $OUT" -bb "$SPEC"

echo "== 产物:"
ls -lh "$OUT" | grep -E "\.(deb|rpm)$" || true
