#!/usr/bin/env bash
# check-pin-freshness.sh — 上游钉版漂移巡检（A 线，ADR freshness-pin-patrol）。
# 我方随包依赖以精确钉版发布（正典 = bundle-runtime-ci.sh 默认值），本脚本：
#   ①内部一致性——三平台 workflow env 与 C# 底线常量的副本必须与正典一致（拦半 bump）；
#   ②上游漂移——npm dist-tags（@deepseek-ai/dsh 取 latest/next 较大者、dshmarket 取 latest）
#     与 Node 现役 LTS 线（nodejs.org index 中 lts≠false 的最大版本，不追 Current）对比钉版；
# 只做感知不做升级：bump 是决策，拍板权留人。
#
# 用法:
#   check-pin-freshness.sh                 # 报告模式：exit 0 无漂移 / 1 上游漂移 / 2 内部不一致 / 3 探测失败
#   check-pin-freshness.sh --annotate      # 注解模式：只打 ::warning，恒 exit 0（release-preflight warn-only 专用）
#   check-pin-freshness.sh --output FILE   # 报告同时写入文件
#   check-pin-freshness.sh --self-test     # 离线夹具自测（scripts/testdata/freshness/）
#
# 数据源可覆写（自测用）：PIN_SH / WF_DIR / GATE_CS / DATA_DIR（设定 DATA_DIR 即离线，
# 从该目录读 registry-dsh.json、registry-dshmarket.json、node-index.json 固定响应）。
set -euo pipefail

MODE=report
OUTPUT=
while [[ $# -gt 0 ]]; do
	case "$1" in
	--annotate) MODE=annotate; shift ;;
	--output) OUTPUT=${2:?--output 需要文件参数}; shift 2 ;;
	--self-test) MODE=selftest; shift ;;
	*) echo "未知参数: $1（可用 --annotate/--output/--self-test）" >&2; exit 64 ;;
	esac
done

SELF=${BASH_SOURCE[0]}
ROOT="$(cd "$(dirname "$SELF")/.." && pwd)"
PIN_SH=${PIN_SH:-$ROOT/scripts/bundle-runtime-ci.sh}
WF_DIR=${WF_DIR:-$ROOT/.github/workflows}
GATE_CS=${GATE_CS:-$ROOT/src/DeepSeek.Harness.Desktop/Services/RuntimeVersionGate.cs}
DATA_DIR=${DATA_DIR:-}

# ---------- 自测入口 ----------
if [[ "$MODE" == selftest ]]; then
	TMPLOG=$(mktemp); trap 'rm -rf "$TMPLOG"' EXIT
	fail=0
	for scene_expected in clean:0 drift:1 inconsistent:2; do
		scene=${scene_expected%%:*}; expected=${scene_expected##*:}
		dir=$ROOT/scripts/testdata/freshness/$scene
		set +e
		DATA_DIR=$dir/data PIN_SH=$dir/bundle-runtime-ci.sh \
			WF_DIR=$dir/workflows GATE_CS=$dir/RuntimeVersionGate.cs \
			bash "$SELF" --output "$TMPLOG" >/dev/null 2>&1
		code=$?
		set -e
		if [[ "$code" == "$expected" ]]; then
			echo "  ok: $scene → exit $code"
		else
			echo "  ✗ $scene → exit $code，期望 $expected"; fail=1
		fi
	done
	if [[ "$fail" == 0 ]]; then echo "== freshness 自测 3 例通过 =="; else echo "== freshness 自测失败 ==" >&2; fi
	exit "$fail"
fi

# ---------- 工具 ----------
if [[ -n "$OUTPUT" ]]; then : > "$OUTPUT"; fi
say() {
	printf '%s\n' "$*"
	[[ -n "$OUTPUT" ]] && printf '%s\n' "$*" >> "$OUTPUT"
	return 0
}
fetch() { # $1=url  $2=离线响应文件名（DATA_DIR 设定时改读该文件）
	local url="$1" offline="$2"
	if [[ -n "$DATA_DIR" ]]; then
		cat "$DATA_DIR/$offline"
	else
		curl -fsSm 15 "$url"
	fi
}

# ---------- ①内部一致性 ----------
extract_default() { # 从 bundle-runtime-ci.sh 提取 KEY="${KEY:-默认值}" 的默认值（纯 bash 剥离，避开 sed 区间语法坑）
	local line
	line=$(grep -m1 "^$1=\"" "$PIN_SH" || true)
	[[ -n "$line" ]] || return 0
	line=${line#*:-}
	printf '%s' "${line%%\}*}"
}

declare -a DSH_COPIES NODE_COPIES MARKET_COPIES
add_copy() { eval "$1+=(\"\$2:\$3\")"; }

pin_node=$(extract_default NODE_VERSION || true)
pin_dsh=$(extract_default DSH_VERSION || true)
if [[ -z "$pin_node" || -z "$pin_dsh" ]]; then
	echo "error: 无法从 $PIN_SH 提取 NODE_VERSION/DSH_VERSION 默认值（正典缺失）" >&2
	exit 3
fi
add_copy NODE_COPIES "bundle-runtime-ci.sh" "$pin_node"
add_copy DSH_COPIES "bundle-runtime-ci.sh" "$pin_dsh"

shopt -s nullglob
wf_files=("$WF_DIR"/package-*.yml)
if [[ ${#wf_files[@]} -eq 0 ]]; then
	echo "error: $WF_DIR 下无 package-*.yml（workflow 副本缺失）" >&2
	exit 3
fi
for f in "${wf_files[@]}"; do
	while IFS= read -r hit; do
		src=${hit%%:*}
		line=${hit#*:}
		key=${line%%:*}; key=${key//[[:space:]]/}
		val=${line##*: }
		val=${val#\'}; val=${val%\'}
		case "$key" in
		NODE_VERSION) add_copy NODE_COPIES "$(basename "$src")" "$val" ;;
		DSH_VERSION) add_copy DSH_COPIES "$(basename "$src")" "$val" ;;
		esac
	done < <(grep -hE '^ *(DSH|NODE)_VERSION:' "$f" | sed "s|^|$f:|" || true)
done

gate_pin=$(grep -o 'MinimumVersion = "[^"]*"' "$GATE_CS" 2>/dev/null | cut -d'"' -f2 || true)
[[ -n "$gate_pin" ]] && add_copy DSH_COPIES "RuntimeVersionGate.MinimumVersion" "$gate_pin"

while IFS= read -r m; do
	MARKET_COPIES+=("bundle-runtime-ci.sh:$m")
done < <({
	grep -ho 'dshmarket@[0-9][0-9.]*' "$PIN_SH" | sed 's/^dshmarket@//' || true
	grep -ho 'dshmarket-[0-9][0-9.]*\.tgz' "$PIN_SH" | sed 's/^dshmarket-//; s/\.tgz$//' || true
} | sort -u)

group_inconsistent() { # $1=组名数组引用名；不一致时打印明细并返回 0
	local -n copies=$1
	[[ ${#copies[@]} -eq 0 ]] && return 1
	local distinct
	distinct=$(printf '%s\n' "${copies[@]}" | cut -d: -f2- | sort -u)
	[[ $(printf '%s\n' "$distinct" | grep -c .) -le 1 ]] && return 1
	say "== 内部不一致：$1 组副本版本分裂（半 bump？）=="
	local c val from
	for c in "${copies[@]}"; do from=${c%%:*}; val=${c#*:}; say "  $1=$val ← $from"; done
	return 0
}

inconsistent=0
group_inconsistent NODE_COPIES && inconsistent=1
group_inconsistent DSH_COPIES && inconsistent=1
group_inconsistent MARKET_COPIES && inconsistent=1
if [[ "$inconsistent" == 1 ]]; then
	[[ "$MODE" == annotate ]] && { echo "::warning::[freshness] 钉版副本内部不一致（半 bump？）——详见上方明细"; exit 0; }
	exit 2
fi

say "== 上游钉版巡检 @$(date -u '+%Y-%m-%dT%H:%MZ') =="

# ---------- ②上游探测 ----------
probe_fail=()
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
fetch_to() { # $1=目标文件 $2=url $3=离线名 $4=依赖名
	if ! fetch "$2" "$3" > "$TMP/$1" 2>/dev/null; then
		probe_fail+=("$4")
		return 1
	fi
	return 0
}

fetch_to dsh.json "https://registry.npmjs.org/-/package/@deepseek-ai/dsh/dist-tags" "registry-dsh.json" "@deepseek-ai/dsh" || true
fetch_to market.json "https://registry.npmjs.org/-/package/dshmarket/dist-tags" "registry-dshmarket.json" "dshmarket" || true
fetch_to node.json "https://nodejs.org/dist/index.json" "node-index.json" "node" || true

verdict=$(python3 - "$pin_dsh" "$pin_node" "$(printf '%s\n' "${MARKET_COPIES[0]}" | cut -d: -f2-)" "$TMP" <<'PY'
import json, sys, os

def parse(v):
    v = str(v).strip().lstrip('v')
    core, _, pre = v.partition('-')
    segs = []
    for s in core.split('.'):
        segs.append((0, int(s), '') if s.isdigit() else (1, 0, s))
    while len(segs) < 3:
        segs.append((0, 0, ''))
    return segs, pre

def newer(a, b):  # a 比 b 新返回 True
    return parse(a) > parse(b)

def level(pinned, cur):
    sp, _ = parse(pinned); sc, _ = parse(cur)
    for i, (x, y) in enumerate(zip(sp, sc)):
        if x != y:
            return ['major', 'minor', 'patch'][i] if i < 3 else 'major'
    return 'prerelease'

def load(name):
    p = os.path.join(sys.argv[4], name)
    try:
        with open(p) as fh:
            return json.load(fh)
    except Exception:
        return None

def out(dep, verdict, detail):
    print('\t'.join([dep, verdict, detail]))

pin_dsh, pin_node, pin_market, _ = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]

dsh = load('dsh.json')
if dsh is None:
    out('dsh', 'unavailable', '')
else:
    cands = [dsh[t] for t in ('latest', 'next') if t in dsh]
    cur = None
    for c in cands:
        if cur is None or newer(c, cur):
            cur = c
    tags = ' '.join(f'{k}={v}' for k, v in sorted(dsh.items()))
    if cur is None:
        out('dsh', 'unavailable', tags)
    elif newer(cur, pin_dsh):
        out('dsh', 'behind-' + level(pin_dsh, cur), f'{tags} → 当前线 {cur}')
    elif newer(pin_dsh, cur):
        out('dsh', 'ahead', f'{tags} → 我方钉版超前（确认是否预期）')
    else:
        out('dsh', 'same', tags)

market = load('market.json')
if market is None:
    out('dshmarket', 'unavailable', '')
elif 'latest' not in market:
    out('dshmarket', 'unavailable', json.dumps(market))
else:
    cur = market['latest']
    if newer(cur, pin_market):
        out('dshmarket', 'behind-' + level(pin_market, cur), f'latest={cur}')
    elif newer(pin_market, cur):
        out('dshmarket', 'ahead', f'latest={cur}')
    else:
        out('dshmarket', 'same', f'latest={cur}')

idx = load('node.json')
if not isinstance(idx, list):
    out('node', 'unavailable', '')
else:
    lts = [(v['version'], v.get('lts')) for v in idx if v.get('lts')]
    if not lts:
        out('node', 'unavailable', 'index 无 lts 条目')
    else:
        ver, codename = lts[0]
        cur = ver.lstrip('v')
        if newer(cur, pin_node):
            out('node', 'behind-' + level(pin_node, cur), f'现役 LTS {ver} ({codename})')
        elif newer(pin_node, cur):
            out('node', 'ahead', f'现役 LTS {ver} ({codename})')
        else:
            out('node', 'same', f'现役 LTS {ver} ({codename})')
PY
)
# 命令替换的闭合括号必须如上独立成行：紧贴写成 `)}` 会把 `}` 并入捕获输出（bash 解析怪癖）。

drift=0
while IFS=$'\t' read -r dep verdict detail; do
	case "$dep" in
	node) human="Node"; pin_label=${NODE_COPIES[0]#*:} ;;
	dsh) human="dsh (@deepseek-ai/dsh)"; pin_label=${DSH_COPIES[0]#*:} ;;
	dshmarket) human="dshmarket"; pin_label=${MARKET_COPIES[0]#*:} ;;
	*) continue ;;
	esac
	case "$verdict" in
	same)
		say "  [$human] 钉版 $pin_label × $detail → 一致"
		;;
	unavailable)
		say "  [$human] 钉版 $pin_label × 上游探测失败"
		;;
	ahead)
		say "  [$human] 钉版 $pin_label × $detail → 钉版超前（信息项）"
		;;
	behind*)
		say "  [$human] 钉版 $pin_label × $detail → **落后（$verdict）**"
		drift=1
		;;
	esac
done <<< "$verdict"

finish() {
	if [[ "$MODE" == annotate ]]; then
		[[ "$drift" == 1 ]] && echo "::warning::[freshness] 上游钉版已漂移（见上方巡检行）——安排一次 bump 拍板；升级注意 minimumReleaseAge 可能拦截过新包"
		[[ ${#probe_fail[@]} -gt 0 ]] && echo "::notice::[freshness] 部分上游探测失败（${probe_fail[*]}），本轮注解不完整"
		exit 0
	fi
	[[ "$inconsistent" == 1 ]] && exit 2
	[[ ${#probe_fail[@]} -gt 0 ]] && exit 3
	[[ "$drift" == 1 ]] && exit 1
	say "== 巡检结论：无漂移 =="
	exit 0
}
finish
