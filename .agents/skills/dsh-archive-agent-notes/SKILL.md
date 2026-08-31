---
name: dsh-archive-agent-notes
description: Use when adding, auditing, pruning, archiving, or reviewing Agent Notes (ADR) in this repo (dotnet-deepseek-harness-desktop) — checks every new note for superseded active records, classifies implemented notes by future decision value, deletes rejected notes that no longer prevent a tempting fallacy, and applies the frozen archived/{kind} rules. This repo is single-language (中文单语; no .zh.md/hash triplet).
---

# Archive Agent Notes (ADR) In This Repo

**This skill is guidance, not a script.** Reduce the active decision corpus without erasing history that can still guide work. Judge every note semantically; word count and age are discovery aids, never archive criteria. This repo is single-language (正文中文单语) — archiving **does not** use the upstream `.zh.md` / `.i18n.yaml` / hash-triplet mechanism; it uses a single `.md` under `.agents/notes/` with `verify-adr-format.py` gating format/lifecycle directory.

## Sources of truth

- [.agents/notes/README.md](../../notes/README.md) — note rules: lifecycle/class/naming, implemented present-tense, Alternatives mandate, evidence rigor.
- Root [AGENTS.md](../../../AGENTS.md)「文档纪律」 — rationale → Agent Notes; durable docs write current state, not change history.
- [scripts/verify-adr-format.py](../../../scripts/verify-adr-format.py) — the machine gate (header/skeleton/state-directory consistency, filename/date rules).

## What earns an archive (classify by future value)

- **Implemented — keep active:** retain a note when its rationale, alternatives, negative guarantees, durable/wire semantics, ownership boundary, security rule, or reintroduction condition is likely to guide a future change. Length does not matter.
- **Implemented — archive:** move to `archived/<kind>/` (with the `Archived:` line) when the shipped decision is complete and its body is unlikely to guide future work — one-off UI chrome, a narrow adapter, a minor closed bug, superseded implementation detail, or process history whose current behavior is obvious elsewhere.
- **Proposed — never archive:** keep a live proposal active; if no longer worth pursuing, reject it with an honest reason (satisfy the rejected lifecycle).
- **Rejected — keep only as a guardrail:** retain a rejection when the losing proposal remains a tempting, meaningful mistake and the note explains why it loses.
- **Rejected — delete:** delete the note when the rejected idea is obsolete, superseded, no longer plausible, or unlikely to prevent re-litigation. Repair or delete inbound links.

Do not archive toward a quota. Inspect every note in scope, classify analogous groups under one principle, and record genuinely borderline decisions for the handoff.

## Check supersession when adding a note

Every new Agent Note triggers a scoped audit of active notes covering the same decision, mechanism, or rejected alternative. Classify each full or partial supersession while writing the new note: archive qualifying implemented notes in the same change, retain and cross-link partial supersessions or independently useful rationale, reject obsolete proposals, and delete rejected notes that no longer prevent a plausible mistake.

## Workflow — Archive one implemented note

1. Move the single `yyyy-mm-dd-<topic>.md` from `implemented/<kind>/` to `archived/<kind>/`; `implemented` is deliberately absent from the archive path.
2. Make no body edits. Insert only `Archived: YYYY-MM-DD` immediately below `Status: implemented`, using the archival date.
3. Search for inbound links from active prose; redirect them to current authority, retarget to the archived path only when the historical snapshot is intentionally cited, or delete them. Never verify or repair links out of the archived note.
4. Run `python3 scripts/verify-adr-format.py` (it checks state-directory consistency and filename/date rules; `archived/` is permanently frozen by the Agent Note rules, not by the script — do not edit an archived note afterward).

## Validation

Run `python3 scripts/verify-adr-format.py` and `git diff --check`; select any additional evidence through [dsh-pre-push-checks](../dsh-pre-push-checks/SKILL.md). Report active implemented notes kept, implemented notes archived, rejected notes kept/deleted, proposed notes rejected if any, and every borderline case with its word count and chosen outcome.
