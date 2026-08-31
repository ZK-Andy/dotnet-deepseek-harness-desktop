---
name: dsh-trim-cot-leakage
description: Use when auditing or fixing prose in this repo (dotnet-deepseek-harness-desktop) that reads like a leaked reasoning transcript — dead design-session citations such as (decision N), audit item codes, or §N of uncommitted drafts; change narration such as "used to", "no longer", "this cut"; stack or review vantage; reviewer-addressed justifications; control-flow narration; or hedged planning residue in comments, docs, or Agent Notes. This repo's durable prose is 中文单语 (bilingual README/user-guide/faq keep .en.md mirrors).
---

# Trimming Chain-of-Thought Leakage

**This skill is guidance, not a script.** Chain-of-thought leakage is prose whose vantage is the authoring session rather than the repository: it cites artifacts only that session could see, narrates the change instead of the state, or argues with a reviewer who has left. The fix is never deletion alone when a passage carries factual clauses — restate each so it stands at HEAD, then delete the transcript around it; a passage carrying none (an audit code, control-flow narration) is deleted outright. [dsh-prose-standard](../dsh-prose-standard/SKILL.md) owns the complete-proposition rule this skill applies.

## The one test

For every suspect passage ask: **could a reader at HEAD, with no access to any session transcript, PR thread, or uncommitted draft, resolve every reference and verify every claim?** If no, restate the surviving facts from the repository's vantage and delete the rest. If yes, it is not leakage — but on current-state surfaces (READMEs, docs, XML doc) a resolvable change story is still change narration, and routes to its sanctioned home (commit/PR/ADR).

## Taxonomy

1. **Dead design-session citations** — `(decision 7)`, `(audit C2)`, `design §4.7`, `plan §1.4`, phase labels (`T4`, `W3`, `P-I`). If the decision has a committed owner (a real ADR path), cite it by name/path; otherwise delete the citation and restate the factual clause to stand alone.
2. **Stack and PR vantage** — "a later PR in this stack", "this PR adds", "the previous commit". State the shipped mechanism or the extension point; deferred work moves to a `TODO`/issue reference.
3. **Change narration and version stamps** — "used to", "no longer", "the old X", indexical stamps ("v1", "this cut", "today"). State the present behavior; a fixed regression becomes a present-tense counterfactual ("without X, Y happens"), never repo history.
4. **Review choreography** — "Rejected in review:", "the reviewer confirmed", draft ordinals, round attributions. Keep the surviving decision/rationale as plain fact; delete who said it when.
5. **Reviewer-addressed justification** — "the cast is safe — it simply…", "this is correct because…". State the invariant that makes the code safe, or delete the comment if the code shows it.
6. **Restatement and derivation transcripts** — control-flow narration ("first we X, then we Y"), test walkthroughs, proofs of obvious branches. Delete; keep only a non-obvious contract or invariant.
7. **Hedges and planning residue** — "probably fine for now", "should be enough", deferrals with no marker. Promote to `TODO`/`FIXME` or restate the actual bound; delete the hedge.
8. **Authoring-language slips** — untranslated working-language fragments in prose whose language is otherwise standard (or the reverse in an `.en.md` mirror). Translate or delete.

## What is not leakage

- **Issue references** — `#1470`, `TODO(name):`, "issue #N owns the follow-up" resolve at HEAD; keep them on any surface.
- **Merged-PR and issue citations inside Agent Notes** — sanctioned evidence per the root「文档纪律」.
- **Suppression justifications** — `catch`-empty explanations, coverage-ignore reasons are required prose; fix a false reason, never delete it.
- **Counterfactual-present regression pins** — "without X, Y happens", "a naive X would…".
- **Measured bounds** — "(measured: 512 nests ≈ 0.15s)"; the provenance word "measured" is load-bearing.
- **Runtime old/new states** — "the old connection drains before the new one accepts" is runtime lifecycle, not change history.
- **Project voice and genre forms** — "we" as project voice; a note's Alternatives-considered section.

## Workflow

1. Scope + exclusions per [dsh-prose-standard](../dsh-prose-standard/SKILL.md): require an explicit scope; never touch `.agents/notes/archived/` (frozen) or recorded fixtures/snapshots.
2. Audit read-only first: grep for suspect patterns (dead `§`/phase labels, "used to"/"no longer", review-choreography phrases, hedges), then judge every hit semantically. Also read the densest prose in scope (module XML doc, READMEs, Agent Notes) without a pattern in hand.
3. Fix owner-first per surface: generated catalogs → fix the source; bilingual pairs → update the `.en.md` counterpart; model-visible strings → wording is behavior, flag for a snapshot-backed change.
4. Before deleting anything, enumerate the passage's propositions (prose-standard) and check the overcorrection traps: trims that flip an obligation into an endorsement, promote a hypothetical to a shipped feature, delete a true fact, or drop provenance.
5. Verify: re-run the scans expecting only sanctioned keeps; confirm every remaining citation resolves at HEAD; run the gates for touched surfaces (`verify-md-links`, `verify-doc-budgets`, `git diff --check`).
