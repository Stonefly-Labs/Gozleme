# Protected `main` — required configuration and verification status

**Verification status: NOT VERIFIED — human action required.**

Repository files cannot prove hosted GitHub settings. On 2026-11-14, during GOZ-0000, the agent
queried the GitHub API read-only:

| Request | Result |
|---|---|
| `GET /repos/Stonefly-Labs/gozleme/rulesets` | HTTP 403 — "Upgrade to GitHub Pro or make this repository public to enable this feature." |
| `GET /repos/Stonefly-Labs/gozleme/branches/main/protection` | HTTP 403 — same message |

Rulesets and branch protection are therefore **not available** on this private repository's
current plan, and no protection of `main` is claimed. The agent did not attempt to change
repository settings.

## Required ruleset for `main` (to be applied by a human owner)

- Target: default branch (`main`); enforcement **Active**.
- Restrict deletions; block force pushes.
- Require a pull request before merging: ≥ 1 approving human review, dismiss stale approvals on
  new commits, require approval of the most recent push, require conversation resolution.
- Require status checks to pass (strict / up to date) — the CI jobs in
  [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml):
  `Governance (registry, prompts, register sync, branch name)`,
  `Backend (build, format, unit + Testcontainers integration)`,
  `Frontend (lint, typecheck, unit, build)`, `Helm chart lint and render`,
  `Secret scan (gitleaks)`, `Containers + autonomous Playwright demo`.
- Require review from Code Owners once real owners are set in `.github/CODEOWNERS`.
- Bypass list: empty (or a named human emergency role whose use is audited). No bots or agent
  credentials in the bypass list.
- Repository settings: disable auto-merge; agent/machine tokens get least privilege without
  merge or admin rights.

## Verification steps (human)

1. Make rulesets available (upgrade to a plan that supports them for private repos, or make the
   repository public).
2. Apply the ruleset above.
3. Verify: `gh api repos/Stonefly-Labs/gozleme/rulesets` lists it as active; attempt a direct
   push to `main` from a non-bypass account and confirm rejection.
4. Record the evidence (date, verifier, API output) here and change the status line to
   **VERIFIED**.
