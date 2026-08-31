---
name: dsh-translate-docs
description: Manually run the bilingual-document workflow in this repo (dotnet-deepseek-harness-desktop) — keeping `foo.md` ↔ `foo.en.md` pairs (README, user-guide, faq) consistent and natural in both languages. This repo's pairing mechanism is a `.md` ↔ `.en.md` naming pair (中文 is the authored/primary side; the `.en.md` is the mirror), not the upstream `.zh.md`/`.i18n.yaml`/hash mechanism.
disable-model-invocation: true
user-invocable: true
---

# Translating This Repo's Bilingual Docs

## Invocation boundary

Run this extended workflow only when the user explicitly invokes `dsh-translate-docs` by name. Never select or load it for ordinary documentation work, from another skill, or from an inferred translation need. Routine translation stays a one-shot, one-pass update of the counterpart.

## What this skill is

**This skill is guidance, not a script.** It is the workflow map for keeping `foo.md` ↔ `foo.en.md` pairs consistent and natural in both languages. In this repo the **中文 `.md` is the authored/primary side** and the **`.en.md` is the mirror** — an edit to the Chinese side obligates an `.en.md` update in the same change. This repo's pairing mechanism is the naming pair (no `.i18n.yaml`/hash/manifest/pnpm), so consistency is maintained by discipline and review, not a pairing gate.

Current paired docs: `README.md` ↔ `README.en.md`, `docs/user-guide.md` ↔ `docs/user-guide.en.md`, `docs/faq.md` ↔ `docs/faq.en.md`. (The rest of `docs/` — architecture, architecture-standards, coding-standards, testing, development, cookbook — is 中文单语 and has no `.en.md` mirror.)

## Triage by change type

- **Update** (pair exists, one side edited): update the counterpart minimally. Do not re-translate a whole document to apply an update — preserve the reviewed phrasing of everything that did not change.
- **New pair** (a `.md` gains an `.en.md` footprint): translate the whole document (see whole-document path).
- **Deleted or renamed doc**: delete or rename the counterpart alongside, or the link/consistency breaks.

## Workflow — Whole-document path (new pairs)

When a translation is written from scratch, the orchestrating agent does not translate: spawn a subagent. The translator reads the sources of truth below, then translates the whole file section by section, locking each section's structure to the source.

### Sources of truth (read, don't re-summarize)

- Root [AGENTS.md](../../../AGENTS.md)「文档纪律」 — prose discipline, current-state, no change history; the `.en.md` mirror must not drift into describing a different state.
- [dsh-prose-standard](../dsh-prose-standard/SKILL.md) — required prose coverage and editorial judgment; apply to both sides without adding/dropping source propositions.
- The owning content doc itself (`README.md`, `docs/user-guide.md`, `docs/faq.md`), the source of truth for what must be said.

### Translate

- **Pass 1 — write, don't transpose.** Read a semantic unit, then restate it natively in the target language. Preserve the required frame without forcing sentence-by-sentence correspondence.
- **Pass 2 — verify against the source, clause by clause.** Confirm nothing was added or dropped, every term is consistent, and each code span/symbol survived verbatim. Fix by rewriting the sentence natively, not by patching words into it.
- **Read the completed counterpart alone** and rewrite phrasing whose awkwardness only shows in isolation.
- Code blocks and commands are byte-identical across the pair. Relative links keep their `.md` targets; only the language-switcher line points to the `.en.md`.

## Finish the pair

1. Language switcher: the Chinese `README.md` H1 area links to `README.en.md` (English); the `.en.md` H1 area links back to `README.md` (中文).
2. The `.en.md` must state the **same shipped state** as the `.md` — no divergent behavior/version facts.
3. In a PR that edits a paired doc, update the counterpart in the same change; the PR body should state which pairs are new vs minimally updated.
4. Run the relevant gates: `python3 scripts/verify-md-links.py`, `python3 scripts/verify-doc-budgets.py --manifest scripts/doc-budgets.manifest.json`, `git diff --check`.

## How to respond to translation review

Follow the [dsh-code-review reporting guidance](../dsh-code-review/SKILL.md#report-findings): evaluate each comment on its merits; for terminology, keep the two sides consistent (the pair is the contract), not only one file.
