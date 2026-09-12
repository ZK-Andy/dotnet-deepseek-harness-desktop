#!/usr/bin/env python3
"""本地 governance 自检（与 CI governance.yml 同逻辑的快检）

检查面：
1. Issue/PR 模板治理字段。
2. 工作流 `run:` 块内禁插值事件载荷（`github.event.*`）及其派生（`${{ env.* }}`）——
   事件载荷一律经 step 级 `env:` 传入脚本（先例 ADR process/2026-09-13-workflow-input-env-interpolation）。
"""
import pathlib, re, sys
root = pathlib.Path(__file__).resolve().parents[1]
ok = True

# 检查 Issue 模板含 Owner/priority/class。
# 豁免：config.yml（表单配置非模板）；test_feedback.yml（30 秒轻量反馈表，
# 治理字段由分诊时补——此前无自动执行点，本脚本对本仓模板长期红着没人发现）。
for p in (root / ".github/ISSUE_TEMPLATE").glob("*.yml"):
    if p.name in ("config.yml", "test_feedback.yml"):
        continue
    t = p.read_text()
    for kw in ["owner", "priority", "class"]:
        if kw not in t.lower():
            print(f"FAIL {p}: 缺 {kw}")
            ok = False
# 检查 PR 模板含 checklist
pr = root / ".github/pull_request_template.md"
if pr.exists():
    t = pr.read_text()
    if "Reviewer Checklist" not in t:
        print("FAIL PR template 缺 Reviewer Checklist")
        ok = False
    if "change-scope" not in t:
        print("FAIL PR template 缺 change-scope")
        ok = False
else:
    print("FAIL 缺 .github/pull_request_template.md")
    ok = False

# 工作流 run: 块禁插值事件载荷与 env 上下文。豁免形态 = step 级 `env:`（载荷进脚本的唯一合法通道）；
# `github.event_name` / `github.ref` 等非载荷上下文不入检查（无攻击者可控面）。
# 表达式体用非贪婪到 `}}`：内层单 `}`（如 format('{0}', …)）不断匹配；`github.event` 不带尾点也命中（整对象 toJSON 同样违规）。
EXPR = re.compile(r"\$\{\{.*?\}\}", re.DOTALL)
BANNED_CTX = re.compile(r"\b(github\.event\b|env\.)")


def _walk_steps(node):
    """递归收集 job 下所有 step（dict）；只沿 jobs/steps 已知键行走，避免误入 strategy/with 嵌套。"""
    if isinstance(node, dict):
        if "jobs" in node:
            for job in node["jobs"].values():
                yield from _walk_steps(job)
        elif "steps" in node and isinstance(node["steps"], list):
            yield from node["steps"]
    elif isinstance(node, list):
        for v in node:
            yield from _walk_steps(v)


def check_workflow_run_interpolation(text):
    """返回 [(step 名, 违规表达式)]；空列表即通过。"""
    try:
        import yaml
    except ImportError:
        print("FAIL PyYAML 不可用（pip install pyyaml），无法解析工作流")
        sys.exit(1)
    doc = yaml.safe_load(text)
    hits = []
    for step in _walk_steps(doc or {}):
        run = step.get("run") if isinstance(step, dict) else None
        if not isinstance(run, str):
            continue
        for m in EXPR.finditer(run):
            if BANNED_CTX.search(m.group(0)):
                hits.append((step.get("name") or step.get("id") or "<unnamed>", m.group(0)))
    return hits


if "--self-test" not in sys.argv:
    for p in sorted((root / ".github/workflows").glob("*.yml")):
        for step, expr in check_workflow_run_interpolation(p.read_text()):
            print(f"FAIL {p}: run: 内插值事件载荷/env 上下文（step「{step}」）：{expr}")
            ok = False
    print("OK" if ok else "FAIL")


def _self_test():
    good = """
jobs:
  a:
    steps:
      - name: ok
        env:
          BODY: ${{ github.event.issue.body }}
        run: echo "$BODY"
"""
    assert check_workflow_run_interpolation(good) == [], "豁免形态被误报"
    bad = """
jobs:
  a:
    steps:
      - name: direct
        run: echo "${{ github.event.issue.body }}"
      - name: env-ctx
        run: echo "${{ env.ISSUE_BODY }}"
      - name: format-brace
        run: echo "${{ format('{0}', github.event.issue.title) }}"
      - name: whole-event
        run: echo "${{ toJSON(github.event) }}"
"""
    assert len(check_workflow_run_interpolation(bad)) == 4, "违规形态漏报"
    nested = """
jobs:
  a:
    steps:
      - uses: x
        with:
          script: |
            run: echo "${{ github.event.pull_request.title }}"
"""
    assert check_workflow_run_interpolation(nested) == [], "with: 块被误报"
    assert check_workflow_run_interpolation(
        "jobs:\n  a:\n    steps:\n      - run: echo \"${{ github.event_name }} ${{ github.ref }}\"\n"
    ) == [], "非载荷上下文被误报"
    print("self-test OK")


if "--self-test" in sys.argv:
    _self_test()
sys.exit(0 if ok else 1)
