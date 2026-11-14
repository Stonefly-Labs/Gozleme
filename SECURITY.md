# Security policy

## Reporting a vulnerability

Please report suspected vulnerabilities **privately** using GitHub's
[private vulnerability reporting](https://docs.github.com/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)
("Report a vulnerability" on the repository's Security tab). Do not open a public issue.

Include affected version/commit, reproduction steps and impact. Maintainers will acknowledge and
coordinate a fix and disclosure. A dedicated security contact address has not yet been
designated; until one is, the private advisory channel is the only supported route.

## Supported versions

Gözleme is pre-release. Only `main` receives fixes.

## Security model

The binding security invariants are in
[`docs/architecture/security-invariants.md`](docs/architecture/security-invariants.md). The
current foundation (GOZ-0000) implements only:

- a non-superuser, non-`BYPASSRLS` application database role separate from the migration role;
- a status API that never returns credentials, connection strings or exception text.

Tenant isolation, authentication, federation trust, revocation and sandboxing are **not yet
implemented**; do not deploy this build to hold real data.
