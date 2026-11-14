# Gözleme agent entry point

1. Read `.gozleme/manifest.yaml` for authoritative document locations; load `docs/architecture/security-invariants.md`, `docs/engineering/agent-workflow.md`, and current `.gozleme/spec-registry.yaml`.
2. Do not work directly on `main`. Only `chore/*`, `feature/*`, `bugfix/*`. **NEVER merge pull requests, enable auto-merge or bypass branch protection.**
3. Installed Spec Kit owns its native `spec.md`, `plan.md`, `tasks.md` and constitution workflow. Read `docs/prompts/const.md` and `docs/prompts/spec-nnnn.md` through manifest and per-spec registry; do not use a custom template.
4. Every spec ends in a real runnable cumulative demo, actual sanitized screenshots when applicable, tests and an evidence-backed PR.
5. After successful convergence, `drive-speckit` MUST invoke the Spec Impact Analyst and Roadmap Steward before preparing PR. Future unimplemented prompts are living; agents may update them within approved architecture, with cited implementation evidence and traceable findings.
6. Agent may finish with `ready-for-human-merge` and must stop. Only after a human merge is verified may the status become `complete`.
7. Never fabricate progress, screenshots, source decisions, repository settings or merge results.
