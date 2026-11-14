# Gözleme — repository-ready architecture and Spec Kit package

This package is intended to be **copied into a new repository** on an allowed working branch (usually `chore/goz-0000-bootstrap`). It is a **documentation/workflow package, not an implemented application**. GitHub rulesets and required-check settings must be applied by a privileged human and subsequently verified.

Start at `.gozleme/manifest.yaml`. Read `docs/architecture/architecture-baseline.md`, `docs/decisions/consistency-review.md`, `docs/roadmap/spec-register.md`, and `docs/prompts/const.md`. The first executable starting prompt is `docs/prompts/spec-0000.md`.

No Gözleme-specific Spec Kit `spec-template.md` is included. Use native Spec Kit templates; configure its generated workspace under `docs/specs` only if the installed tooling supports this. The prompt files are **living**, revision-controlled inputs until their spec is implemented and merged.

## Initial use

1. On a `chore/*` branch, copy package into the repo; install/initialize your existing Spec Kit environment without reinstalling it.
2. Run `python3 scripts/validate_docs.py` (requires PyYAML and jsonschema) and `python3 scripts/generate_register.py --check`.
3. Process `docs/prompts/const.md` with the installed Spec Kit constitution command.
4. Invoke `drive-speckit` for `GOZ-0000`, which reads `.gozleme/manifest.yaml`, the registry entry and `docs/prompts/spec-0000.md`.
5. Review the evidence-backed PR and **merge it yourself**; agents must never merge.

## Source fidelity

The prior conversation confirms **95 decisions**, but exact verbatim source text for every early Round 2 decision was not accessible for this package. `docs/decisions/decision-register.md` explicitly marks unavailable per-decision source text; it does not invent historical quotations. Round 3 decisions and the cross-cutting architecture are summarized from confirmed conversation context. Reconcile historical source records before treating every individual decision row as fully source-verified.
