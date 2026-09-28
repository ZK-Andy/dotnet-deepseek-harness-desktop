#!/usr/bin/env bash
# install-linters.sh — 装脚本层静态检查工具（shellcheck + actionlint），版本与 sha256 双钉。
# 为什么钉：shellcheck 的诊断集合随版本漂移（新版新增检查会让本地与 CI 结论分叉，
# 「本地绿 CI 红」正是门禁可信度最大的敌人），故本地与 CI 用同一版本；摘要在版本库硬编码
# （取自 GitHub releases API 的 asset digest）——改用 API 现取会让「版本换了、摘要没换」
# 这类漂移只在运行时才暴露。
# 平台：仅 linux x86_64（CI runner 与开发沙箱）。其它平台按提示用包管理器装同版本号，
# 版本一致性由 docs/script-standards.md 的「工具版本」小节声明。
# 用法: install-linters.sh [目标目录（默认 <仓库>/.cache/bin）]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
source "$SCRIPT_DIR/lib/common.sh"

SHELLCHECK_VERSION=0.11.0
SHELLCHECK_SHA256=b7af85e41cc99489dcc21d66c6d5f3685138f06d34651e6d34b42ec6d54fe6f6
ACTIONLINT_VERSION=1.7.12
ACTIONLINT_SHA256=8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8

GH_RELEASES=https://github.com

# fetch_verify <工具名> <资产 URL> <sha256> <归档内路径> <目标目录>
# 下载 → 校验摘要 → 解出单文件 → 落盘 0755。摘要不符即拒绝安装（不落半个工具）。
fetch_verify() {
  local name="$1" url="$2" sha="$3" member="$4" dest="$5" tmp
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' RETURN
  if [[ -x "$dest/$name" ]] && "$dest/$name" --version 2>/dev/null | grep -qF "$(case "$name" in shellcheck) echo "$SHELLCHECK_VERSION" ;; *) echo "$ACTIONLINT_VERSION" ;; esac)"; then
    echo "  $name 已是钉版（$( "$dest/$name" --version 2>/dev/null | head -1)），跳过"
    return 0
  fi
  echo "  下载 $name..."
  curl -sSfL --retry 2 -o "$tmp/asset" "$url" || die "$name 下载失败: $url"
  if ! printf '%s  %s\n' "$sha" "$tmp/asset" | sha256sum -c - >/dev/null 2>&1; then
    die "$name 摘要不符（期望 sha256:$sha）——拒绝安装，检查上游资产是否被替换"
  fi
  tar -xzf "$tmp/asset" -C "$tmp" "$member" || die "$name 归档内缺 $member"
  mkdir -p "$dest"
  install -m 0755 "$tmp/$member" "$dest/$name"
  echo "  已装: $dest/$name"
}

if [[ "$(uname -s)" != "Linux" || "$(uname -m)" != "x86_64" ]]; then
  die "本脚本只装 linux x86_64 二进制；其它平台请按 docs/script-standards.md 的「工具版本」装同版本（shellcheck $SHELLCHECK_VERSION / actionlint $ACTIONLINT_VERSION）"
fi

DEST="${1:-$SCRIPT_DIR/../.cache/bin}"
mkdir -p "$DEST"
fetch_verify shellcheck \
  "$GH_RELEASES/koalaman/shellcheck/releases/download/v$SHELLCHECK_VERSION/shellcheck-v$SHELLCHECK_VERSION.linux.x86_64.tar.gz" \
  "$SHELLCHECK_SHA256" "shellcheck-v$SHELLCHECK_VERSION/shellcheck" "$DEST"
fetch_verify actionlint \
  "$GH_RELEASES/rhysd/actionlint/releases/download/v$ACTIONLINT_VERSION/actionlint_${ACTIONLINT_VERSION}_linux_amd64.tar.gz" \
  "$ACTIONLINT_SHA256" actionlint "$DEST"
echo "== 工具就位：$DEST（shellcheck $SHELLCHECK_VERSION / actionlint $ACTIONLINT_VERSION）"
