---
name: dsh-doc-standards
description: Use when writing, moving, reviewing, or auditing documentation in this repo (dotnet-deepseek-harness-desktop) — choosing placement/tier, separating tutorial from reference, trimming doc slop, responding to a verify-doc-budgets/verify-md-links failure, or "improve/audit the docs". The doc rules live in the root AGENTS.md「文档纪律」+ the docs/ tier (addressable: architecture / coding-standards / testing / cookbook).
---

# Applying The Documentation Standard In This Repo

**This skill is guidance, not a script.** The documentation rules live in the root [AGENTS.md](../../../AGENTS.md)「文档纪律」and the docs tier in `docs/`. This workflow covers placement, corpus audits, budgets, and validation across Markdown, XML doc, and code comments. This repo is .NET (XML doc, not JSDoc) with no `docs/AGENTS.md` — use the root AGENTS + `docs/*` as the authority. Use [dsh-prose-standard](../dsh-prose-standard/SKILL.md) for required coverage and editorial judgment; never treat length alone as a defect.

## Sources of truth (read, don't re-summarize)

- Root [AGENTS.md](../../../AGENTS.md)「文档纪律」 — tier taxonomy (every fact one home), write-current-state, relative links + machine-checkable, no bare filenames;「字数预算」table.
- [docs/cookbook.md](../../../docs/cookbook.md) — procedure/pitfalls (stage-tagged entries).
- [.agents/notes/README.md](../../notes/README.md) — when a decision earns an Agent Note and what goes inside one; [docs/architecture-standards.md](../../../docs/architecture-standards.md) / [docs/coding-standards.md](../../../docs/coding-standards.md) for structure/behavior contracts.
- [scripts/verify-doc-budgets.py](../../../scripts/verify-doc-budgets.py) / [verify-md-links.py](../../../scripts/verify-md-links.py) / [verify-cookbook.py](../../../scripts/verify-cookbook.py) — the machine gates.

## Review structure before prose

Apply the tier discipline to every human-facing document in scope. Do not apply this structural pass to Agent Notes.

1. Locate the document in the repo/navigation. State its subject and identify its direct children.
2. Set the permitted detail: keep full detail on the subject; summarize direct children by purpose/responsibility; move deeper explanations to their owning descendants with links.
3. Classify the doc from intended use, not path/title. A tutorial leads through ordered work to an observable outcome; a reference supports lookup within an explicit scope without sequential reading.
4. Split substantial mixed forms; put a small secondary form in a clearly labeled section.

Then check placement costs:

- A move is atomic: remove from the old home, add to the new home, fix every inbound link in the same change.
- Generated catalogs are never hand-edited; change the generator's source.
- Before renaming/moving a doc, grep inbound references (`verify-md-links` catches Markdown link targets + `#fragment` anchors).

## Workflow — Audit the corpus

After the structural pass, hunt the standard's slop checklist with the cheapest probes first. Determine the change scope (`scripts/change-scope.sh <base> <head>`) before semantic judgment; re-run after a retarget or base merge.

1. Measure: `python3 scripts/verify-doc-budgets.py --manifest scripts/doc-budgets.manifest.json`, then `git ls-files '*.md' | xargs wc -w | sort -rn | head -30` for unbudgeted outliers.
2. Hunt reasoning-transcript leakage (narrated history, dead design citations, review choreography, control-flow narration, test walkthroughs) with [dsh-trim-cot-leakage](../dsh-trim-cot-leakage/SKILL.md).
3. Hunt duplication by grepping distinctive phrases; keep one home, replace other copies with links.
4. Replace hand-written catalogs/test-status inventories with the authoritative tree/script/generated reference.
5. In `implemented/` Agent Notes, remove migration plans/acceptance-checklists/future-tense spec language; keep concise verification contracts.

Exclude `.agents/notes/archived/` from audits and edits. Keep every load-bearing rule as one-to-three lines plus a link to its rationale; cut stories, duplicates, and the path used to derive the rule.

## When verify-doc-budgets goes red

Apply the ordered relocate-condense-raise policy in root `AGENTS.md`「字数预算」; this skill only supplies the workflow probes above. A budget overrun is resolved by moving to another tier (leave a link), then condensing, then raising the allowance (with PR justification) — not by silently exceeding it.

## Validation and PR hygiene

Run `python3 scripts/verify-doc-budgets.py --manifest scripts/doc-budgets.manifest.json`, `python3 scripts/verify-md-links.py`, `python3 scripts/verify-cookbook.py`, and `git diff --check`. If a `.en.md` counterpart changed, update the counterpart in the same change. The PR body should give word deltas, explain any deliberately long exception, and list checks.
