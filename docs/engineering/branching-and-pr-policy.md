# Git, branch protection and human merge policy

All agent development must occur on `chore/*`, `feature/*` or `bugfix/*` branches. No agent direct pushes to `main`; no agent may merge a PR (own or others'), enable auto-merge, bypass branch protection, dismiss required reviews, override checks or claim human approval. Agents can commit to working branches, push, open/update PRs and prepare review-ready evidence.

Protect `main` using actual GitHub repository rulesets or branch protection, not merely a `CODEOWNERS` file: require pull requests, human reviewer(s), passing CI/security checks, resolved review threads, no force push/deletion, and restrict bypass. Configure machine credentials with least privilege and, where supported, no merge capability. A human owner must verify ruleset enforcement; repo files cannot prove hosted settings. Only a human merges, and the registry marks complete only after independently verifying merged commit on main. Human emergency overrides must be separately audited and cannot be made by agents.

Recommended names: `chore/goz-0000-bootstrap`, `feature/goz-0015-local-semantic-search`, `bugfix/goz-0015-private-result-filter`. PR must list scope, spec IDs, contracts/migrations, tests, autonomous demo execution, embedded actual sanitized screenshots, downstream impact report, prompt mutations, human decisions and merge blockers.
