# Claude Code — Gözleme

Read `AGENTS.md` and `.gozleme/manifest.yaml` first. Native Claude skills are in `.claude/skills/`, agents in `.claude/agents/`. Use `drive-speckit` for all specification work. Do not reinstall installed Spec Kit; use native templates and commands. Project docs and prompts reside under `docs/`, root `.gozleme/` holds the machine-readable registry and discovery metadata.

**Hard Git prohibition:** Claude agents must **never** push directly to `main`, merge any PR, enable auto-merge or bypass protections. Work only `chore/*`, `feature/*`, `bugfix/*`. Human maintainer alone merges.

**Nonnegotiable:** runnable demo + automated browser/UI/API evidence + sanitized screenshots in PR whenever visual, and mandatory post-convergence Spec Impact Analyst with updates to living future prompt files, Roadmap Steward registry sync and human approvals for material governance changes. See `docs/engineering/agent-workflow.md`.
