#!/usr/bin/env bash
# package-linux.sh — 从 .NET publish 输出打 Linux 安装包（deb + rpm）。
# 参照 pilot-harness apps/desktop/electron-builder.yml 的 Linux 产物理念：
#   linux.desktop（Name/Comment/Categories/StartupWMClass）
#   产物为 AppImage→deb/rpm（此处为 .NET 自包含 publish 的等价物）
# online-first（ADR online-first-unbundled-runtime）：包只带壳 + 安装器自带插件资源
# （resources/plugins/dsh-desktop-companion.tgz）；运行时 = 用户 PATH 全局 dsh，不再捆绑闭包。
# 布局：
#   usr/lib/<app>/   = dotnet publish 全量 + resources/plugins（插件 tgz）
#   usr/bin/<app>    = 符号链接
#   usr/share/applications/<app>.desktop（对齐 pilot-harness linux.desktop）
# 用法：
#   scripts/package-linux.sh [publish_dir]          # 全量（需 dpkg-deb + rpmbuild；Ubuntu runner 自带 dpkg-deb，rpm 需 apt 安装）
#   scripts/package-linux.sh --stage-only [dir]     # 仅组装 staging，供无工具机校验布局
#   scripts/package-linux.sh --self-test            # 离线夹具（共用头部 + 架构映射）
# 环境：VERSION（默认 0.1.0，CI 由 tag/inputs.version 注入）、MAINTAINER、ARCH（amd64/x86_64/arm64/aarch64）
set -euo pipefail

ROOT_SCRIPTS="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/packaging-common.sh
source "$ROOT_SCRIPTS/lib/packaging-common.sh"

if [[ "${1:-}" == "--self-test" ]]; then
  packaging_self_test
  exit $?
fi

# CLI 解析 / ARCH 归一化 / VERSION / PUBLISH_DIR / OUT 由共享头部统一处理
# （scripts/lib/packaging-common.sh；-前闭包残留与主程序判据唯一家在 verify-package-layout.sh）。
packaging_init linux "$@"
APP="deepseek-harness-desktop"
MAINTAINER="${MAINTAINER:-zhangkun <253117546@qq.com>}"
STAGE="$OUT/stage/$APP-$VERSION"

echo "== 组装 staging: $STAGE"
DEST="usr/lib/$APP"
rm -rf "$STAGE" && mkdir -p "$STAGE/$DEST"

# 1) publish 全量（dotnet 自包含，含 saucer/lib* 等原生依赖）
cp -r "$PUBLISH_DIR/." "$STAGE/$DEST/"

# 2) 安装器自带插件资源（resources/plugins）：companion tgz 从仓库源码现打并校验（fail loud）。
#    不再捆绑运行时闭包——首启引导确保全局 dsh（ADR simple-shell-single-global-dsh）。
mkdir -p "$STAGE/$DEST/resources/plugins"
bash "$ROOT/scripts/build-companion-tgz.sh" "$STAGE/$DEST/resources/plugins/dsh-desktop-companion.tgz"
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
StartupWMClass=io.github.ZK-Andy.dotnet-deepseek-harness-desktop
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

# 4) 布局断言（staging 即 deb/rpm 的内容源）：闭包残留 / 插件资源 / 主程序 + 可执行位
packaging_assert_layout linux "$STAGE/$DEST"

echo "== staging 体积: $(du -sh "$STAGE" | cut -f1)"
if [[ $STAGE_ONLY -eq 1 ]]; then
  echo "(--stage-only 校验布局)："
  find "$STAGE" -maxdepth 3 -type d | sort | head -30 || true
  echo "--- 插件资源 ---"
  ls -lh "$STAGE/$DEST/resources/plugins/" 2>&1 | head -3 || true
  exit 0
fi

command -v dpkg-deb >/dev/null || die "缺 dpkg-deb（Ubuntu runner 自带；本地可用 --stage-only 校验）"
command -v rpmbuild >/dev/null || die "缺 rpmbuild（Ubuntu: sudo apt-get install -y rpm）"

echo "== [deb]"
mkdir -p "$STAGE/DEBIAN"
cat > "$STAGE/DEBIAN/control" <<EOF
Package: $APP
Version: $VERSION
Section: devel
Priority: optional
Architecture: $ARCH
Maintainer: $MAINTAINER
Depends: libwebkitgtk-6.0-4, libadwaita-1-0
Description: DeepSeek Harness Desktop for .NET (native shell, online-first runtime bootstrap)
EOF
mkdir -p "$OUT"
# 文件名加入 linux 标识，避免与 macos/windows 混淆（原 _amd64.deb 无平台前缀；deb 控制内 Architecture 仍为 amd64/arm64，文件名仅作发布区分）
dpkg-deb --root-owner-group --build "$STAGE" "$OUT/${APP}_${VERSION}_linux-${ARCH}.deb"

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
BuildArch: $RPM_ARCH
# 参照 pilot-harness asar:false 与数万文件闭包：rpm 自动依赖扫描会把 node_modules 跨平台 prebuild
#（aarch64/musl/ld-linux/perl 等）误判为运行依赖，导致 dnf 安装失败 → 整体禁用自动依赖，显式声明真实依赖。
AutoReqProv: no
# saucer 动态链接 libwebkitgtk-6.0.so.4（WebKitGTK 6 / GTK4）与 libadwaita-1.so.0（壳窗口
# 直接依赖；minimal 系统缺它会在 Run 即 DllNotFound——2026-08-29 冒烟实证）。两者必须写带
# (()(64bit)) 限定的规范形：裸名在 aarch64 上无任何提供者（v0.3.4 arm64 冒烟实证 nothing
# provides），在 x86_64 上则被 i686 多库包侥幸满足、拉进整套 32 位栈——限定名两架构都精确命中。
Requires: libwebkitgtk-6.0.so.4()(64bit), libadwaita-1.so.0()(64bit)
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
# 重命名去掉 Release 后缀并加入 linux 标识（发布区分；rpm 内 BuildArch 仍为 x86_64/aarch64）
for f in "$OUT/rpmbuild/RPMS"/*/*.rpm; do
  [[ -f "$f" ]] || continue
  if [[ "$f" == *"-1."* ]]; then
    mv "$f" "${f/-1./.}"
    f="${f/-1./.}"
  fi
  # deepseek-harness-desktop-0.1.15.x86_64.rpm → deepseek-harness-desktop-0.1.15_linux-x86_64.rpm
  if [[ "$f" != *"_linux-"* && "$f" != *"-linux."* ]]; then
    dir="$(dirname "$f")"
    base="$(basename "$f")"
    # base: deepseek-harness-desktop-0.1.15.x86_64.rpm → deepseek-harness-desktop-0.1.15_linux-x86_64.rpm
    newbase="${base/.${RPM_ARCH}.rpm/_linux-${RPM_ARCH}.rpm}"
    if [[ "$newbase" != "$base" ]]; then
      mv "$f" "$dir/$newbase"
    fi
  fi
done
# 将 rpm 移到 $OUT 顶层以便统一产物清单与 CI 上传（mv 而非 cp：源头不留副本，
# 否则 rpmbuild 嵌套目录与顶层同名 rpm 双份进 Release——上传/发布 glob 会各取一份）
mkdir -p "$OUT"
for f in "$OUT"/rpmbuild/RPMS/*/*.rpm; do
  [[ -f "$f" ]] || continue
  mv -f "$f" "$OUT/"
done

echo "== 产物:"
shopt -s nullglob
for f in "$OUT"/*.deb "$OUT"/*.rpm; do ls -lh "$f"; done
shopt -u nullglob
echo "== deb 校验（如可用）:"
dpkg-deb -I "$OUT/${APP}_${VERSION}_linux-${ARCH}.deb" 2>&1 | head -20 || true
echo "== rpm 校验（如可用）:"
rpm -qp --requires "$OUT"/*.rpm 2>&1 | head -30 || true
