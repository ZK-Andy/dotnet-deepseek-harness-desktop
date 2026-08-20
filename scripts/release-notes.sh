#!/usr/bin/env bash
# release-notes.sh — 从 git log 生成结构化 Release 正文（中英小节，按 conventional commit 类型归类）。
# 用法：
#   bash scripts/release-notes.sh [from_ref] [to_ref]     # 例：bash scripts/release-notes.sh v0.1.15 v0.1.16
#   bash scripts/release-notes.sh HEAD~10 HEAD            # 也可用范围
# 省略 from_ref 时自动取当前 tag 的前一个 tag；省略 to_ref 默认 HEAD。
# 输出：markdown 正文到 stdout（供 release.yml 的 body 使用）。中文为主 + 英文小节头，与仓库「默认中文」一致。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REPO_URL="https://github.com/ZK-Andy/dotnet-deepseek-harness-desktop"

FROM_REF="${1:-}"
TO_REF="${2:-HEAD}"
if [[ -z "$FROM_REF" ]]; then
  # 上一个可达 tag（<to 或 <=to 之前的 tag）
  FROM_REF="$(git -C "$ROOT" describe --tags --abbrev=0 "$TO_REF^" 2>/dev/null || echo "")"
  if [[ -z "$FROM_REF" ]]; then FROM_REF="$(git -C "$ROOT" rev-list --max-parents=0 HEAD 2>/dev/null || echo "")"; fi
fi
[[ -n "$FROM_REF" ]] || { echo "error: 无法确定比较起点（from_ref）" >&2; exit 1; }

# 断言：to_ref 必须能从 from_ref 演进（避免向上比较返回空）
if ! git -C "$ROOT" merge-base --is-ancestor "$FROM_REF" "$TO_REF" 2>/dev/null; then
  echo "error: $FROM_REF 不是 $TO_REF 的祖先（范围无效）" >&2; exit 1
fi

LOGS="$(git -C "$ROOT" log --no-merges --format='%h|%s' "$FROM_REF..$TO_REF" 2>/dev/null || true)"

# 归类：按 subject 前缀区分类型；无前缀归「其他」。转义 markdown 特殊字符，括号[x]保留。
bucket() {
  local type="$1" title="$2" etitle="$3"
  local pat="$4" nons_config="$5"
  echo
  echo "## $title ($etitle)"
  echo
  local line short rest re
  re='^[A-Za-z]+(\([^)]*\))?:'
  while IFS='|' read -r short rest; do
    [[ -n "$rest" ]] || continue
    local t="${rest%%:*}"
    local show="$rest"
    # 去类型前缀，保留括号内的 scope（如 feat(foo):）
    if [[ "$rest" =~ $re ]]; then
      show="${rest#*: }"; [[ "$show" == "$rest" ]] && show="${rest##*:}"
    fi
    case "$type" in
      feat)  if [[ "$t" == feat* ]];  then echo "- $show (\`\`\`$short\`\`\`)"; fi ;;
      fix)   if [[ "$t" == fix* ]];   then echo "- $show (\`\`\`$short\`\`\`)"; fi ;;
      perf)  if [[ "$t" == perf* ]];  then echo "- $show (\`\`\`$short\`\`\`)"; fi ;;
      docs)  if [[ "$t" == docs* ]];  then echo "- $show (\`\`\`$short\`\`\`)"; fi ;;
      chore) if [[ "$t" == chore* || "$t" == build* || "$t" == ci* ]]; then echo "- $show (\`\`\`$short\`\`\`)"; fi ;;
    esac
  done <<< "$LOGS"
}

# 首行：标题
echo "# Release $TO_REF"
echo
echo "DeepSeek Harness Desktop 桌面客户端发布包。安装包见下方 Assets；校验见 \`SHA256SUMS\`。"
echo

bucket feat   "新增" "New Features"   "feat" ""
bucket fix    "修复" "Bug Fixes"      "fix"  ""
bucket perf   "优化" "Performance"    "perf" ""
bucket docs   "文档" "Docs"           "docs" ""
bucket chore  "构建 · CI · 其他" "Build · CI · Other" "chore" ""

echo
echo "**Full Changelog**: $REPO_URL/compare/$FROM_REF...$TO_REF"
