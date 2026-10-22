#!/usr/bin/env bash
# change-scope.sh — 输出变更范围（评审 / push 前的最小证据前置，替代 deepseek 原仓库的 change-scope）。
#
# 输出三部分：
#   base  /  head  —— 本次范围的两个锚点
#   commits      —— base..head 的提交列表
#   changed      —— 涉及的路径：已提交 diff + 已暂存 diff + 工作区未暂存 diff + 未跟踪文件（dedupe）
#
# 用法：
#   ./scripts/change-scope.sh [<base-ref> [<head-ref>]]   # 显式指定（首选）
#   ./scripts/change-scope.sh                             # 自动推导 base：origin/main 优先
#
# 原则（来自 dsh-pre-push-checks 的最小证据）：绝不臆测 base，能用显式 ref 就用显式 ref；
# 自动推导仅作为便利，结果需人工确认。
set -euo pipefail

BASE=""
HEAD="${2:-HEAD}"
if [[ $# -ge 1 ]]; then
  BASE="$1"
else
  # 缺省与上游主干比：`git merge-base --fork-point HEAD` 不给上游时拿 HEAD 自己的 reflog
  # 当上游，会返回 HEAD 本身或已被丢弃的提交，打印「base == head」的空范围
  # （pre-push 的取证输出因此一度看起来像零变更）。
  if git rev-parse --verify --quiet origin/main >/dev/null 2>&1; then
    BASE="$(git merge-base origin/main HEAD 2>/dev/null || true)"
  fi
  [[ -z "$BASE" ]] && BASE="$(git merge-base --fork-point origin/main HEAD 2>/dev/null || true)"
  [[ -z "$BASE" ]] && BASE="$(git rev-list --max-parents=0 HEAD | tail -1 || true)"
  [[ -z "$BASE" ]] && { echo "error: 无法确定 base（仓库还没有历史？）" >&2; exit 1; }
  if [[ "$BASE" == "$(git rev-parse HEAD)" ]] && [[ -z "$(git status --porcelain)" ]]; then
    echo "warning: 缺省 base 解析到 HEAD（无可比区间），请显式传 <base-ref>" >&2
  fi
fi

echo "== base:  $BASE  ($(git rev-parse --short "$BASE" 2>/dev/null || echo '?')${2:-})"
echo "== head:  $HEAD  ($(git rev-parse --short "$HEAD" 2>/dev/null || echo '?')${1:+ from arg})"
echo
echo "== commits ($BASE..$HEAD):"
git log --oneline "$BASE..$HEAD" || true
echo
echo "== changed paths:"
{
  git diff --name-only "$BASE...$HEAD" 2>/dev/null
  git diff --cached --name-only
  git diff --name-only
  git ls-files --others --exclude-standard
} | sort -u
