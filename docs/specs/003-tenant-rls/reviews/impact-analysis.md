# GOZ-0003 post-convergence spec impact analysis

**Source spec:** GOZ-0003, Tenant RLS and visibility scaffolding
**Implementation revision:** analysed at `9131bda` on `feature/goz-0003-tenant-rls`. The code last changed at `424a15e`; `9131bda` adds only evidence and progress files.
- The cumulative demo ran at `424a15e` on a fresh stack with a clean tree. 33 of 33 assertions passed, with the GOZ-0000, GOZ-0001 and GOZ-0002 suites replayed (`evidence/demo-run.json`, `evidence/rls-evidence.json`).
- drive-speckit reports 256 .NET tests and 131 web tests passing at `424a15e`.
**Analyst:** Spec Impact Analyst (`analyze-spec-impact`), 2026-11-15
**Structured findings:** `docs/specs/003-tenant-rls/reviews/impact-findings.json` (18 findings; validates against `.gozleme/schemas/impact-findings.schema.json`)

## Scope inspected

- **Migration:** `src/Gozleme.Api/Persistence/Migrations/20261117000000_TenantRls.cs`, read in full: `gozleme_private`, `verify_context`, the helpers, the `rls_internal()` guard with the `gozleme.rls_helper` amendment, the grants, FORCE, and every policy.
- **Tenancy code:** `src/Gozleme.Api/Tenancy/*` (options, signer, `TenantContext`, `TenantTransaction`, telemetry, self-check).
- **Catalog code:** `Persistence/TableClassification.cs`, `Persistence/RlsCatalog.cs` and `Persistence/MigrationRunner.cs` (key provisioning).
- **Changed application code:** `OrganizationEndpoints.cs` and `OrganizationAccess.cs`, `Identity/OperatorCommands.cs` (seed), `Identity/SecurityAudit.cs` (1201–1203) and `GozlemeApp.cs`.
- **Deployment and docs:** `compose.yaml`, the Helm README and templates, `.gitleaks.toml`, `docs/engineering/local-development.md`.
- **Tests:**
  - integration: `RlsCatalogTests`, `RlsIsolationTests`, `TenantContextBoundaryTests`, `TenantSeedTests` and `TenantTelemetryTests`;
  - unit: `TenantContextSignerTests`;
  - infrastructure: `Infrastructure/{GozlemeDatabase,TwoTenants}.cs` and the narrowed `BootstrapBoundaryTests`;
  - `AuthFlowTests.Every_sign_in_issues_a_new_session_and_retires_the_previous_one`, for OBS-01.
- **Demo:** `e2e/tests/tenant-rls-demo.spec.ts`, plus the `evidence.ts`, `compose.ts`, `playwright.config.ts` and `bootstrap-demo.spec.ts` changes.
- **Spec workspace:**
  - spec.md (FR-001 to FR-032, SC-001 to SC-006) and plan.md;
  - research.md R1 to R14, including the R4 amendment;
  - data-model.md and contracts/tenant-context.md;
  - tasks.md and progress.yaml, including the decisions and GOZ-0003-OBS-01.
- **Governance:** `security-invariants.md`, `consistency-review.md` (CR-02), `architecture-baseline.md`, `.gozleme/spec-registry.yaml` (DAG).
- **Downstream prompts:** every prompt from GOZ-0004 to GOZ-0041:
  - GOZ-0004 to GOZ-0039 depend on GOZ-0003 transitively.
  - GOZ-0040 and GOZ-0041 do not depend on it, but they will merge onto a `main` that runs `RlsCatalogTests`.

## Verified implementation facts that downstream specs consume

| Area | Fact at `9131bda` (evidence) |
|---|---|
| RLS | Enabled **and forced** on `organizations`, `memberships` and `invitations` (`TenantRls.cs:160-165`). There is a policy for each command, and each predicate is `CASE WHEN gozleme.rls_internal() THEN true ELSE … END`. Organizations have no DELETE policy. Row rules are member-level: any member may add or delete memberships and manage invitations in its organization. `memberships` UPDATE admits only the account's own row (`:198-202`). |
| Context | `v1.<account>.<unix-expires>.<hex HMAC-SHA256>`, issued for 5 minutes. The database accepts it while `now() < exp ≤ now()+10 min`, and `now()` is the transaction start time. The token names an account only. It is stored transaction-locally by `establish_context` and re-verified on every `current_account_id()` call. |
| Key | Configured as `Gozleme:Rls:ContextKey`, at least 32 characters, needed by `api`, `migrate` and `seed`. The migrator upserts it into `gozleme_private.context_keys` (a single row) **after** migrations run. `gozleme_app` has no privilege on `gozleme_private`. |
| Guard | `rls_internal()` is `current_user = owner AND (session_user <> owner OR gozleme.rls_helper = 'on')`. It is true inside **any** migrator-owned definer function called by another login. The pinned definer list is `TableClassification.DefinerHelpers`. |
| Request model | `TenantTransaction` is an endpoint filter on the `/organizations` and `/invitations` groups only (`OrganizationEndpoints.cs:28,42`). It runs one transaction per request, defers audits with `AfterCommit`, and maps SQLSTATE `42501` to an empty 404, `refused_write` and audit 1202. A null principal gives a `missing` denial. |
| Catalog | `RlsCatalog.ViolationsAsync` checks schema `gozleme`, relkinds `r`/`p` only, that tenant tables have RLS, FORCE and at least one policy, that `SharedPublic` is empty, the definer pin for `gozleme`/`gozleme_private` only, `PUBLIC` execute, and `gozleme_private` privileges. |
| Telemetry | Span `gozleme.tenant.transaction` with events `…context.established` and `…context.cleared`. Counter `gozleme.tenant.denials{gozleme.tenant.denial.reason}` with reasons {missing, invalid, expired, refused_write}. Audit events 1201–1203 (range 1201–1210). `gozleme.organization.id` stays on `gozleme.org.*` spans only. |
| Seed and demo | `seed` acts through a seeded member's signed context. It adds `dave` and **Seeded Partner Org** `0193a000-0000-7000-8000-000000000003`, a second tenant disjoint from Seeded Demo Org. The e2e helpers are `DEMO.dave` and `PARTNER_ORG`. The `goz-0003` project depends on `goz-0002`. Its suite restarts the API with a mismatched key and restores it. |

## Findings

| ID | Sev | Finding | Affected specs | Disposition |
|---|---|---|---|---|
| GOZ-0003-IF-01 | high | Rules for new tenant tables and functions (FORCE, CASE shape, classification, isolation matrix, revoking `PUBLIC` execute, no definers) and the migration pins | 0004–0041 | applied to prompts |
| GOZ-0003-IF-02 | high | The tenant transaction is per route group and spans the whole handler: endpoints must opt in, use the ambient transaction, and keep I/O out | 0004–0039 (notably 0009–0011, 0016, 0031, 0032) | applied to prompts |
| GOZ-0003-IF-03 | high | Data migrations under FORCE silently change zero rows. "Data migrations sign a context" cannot express a cross-tenant backfill such as GOZ-0004's initial roles | 0004, 0014, 0017, 0035, 0039 | **human decision** (before the GOZ-0004 plan) |
| GOZ-0003-IF-04 | high | No tenant context for non-account principals (workers, sync appliers, sweeps, federation peers) | 0015–0017, 0029–0036 | **human decision** (by GOZ-0015 at the latest) |
| GOZ-0003-IF-05 | high | GOZ-0004 roles collide with the member-level write rules: an UPDATE to another member's row is silently filtered and answers 500 | 0004 | applied to prompts |
| GOZ-0003-IF-06 | medium | RLS does not hide foreign-key or unique-constraint checks, so they are cross-tenant existence oracles | 0005, 0012, 0014, 0024, 0034 | applied to prompts |
| GOZ-0003-IF-07 | medium | `SharedPublic` is reserved until GOZ-0012, so catalog tables in GOZ-0005 to GOZ-0011 are tenant-owned. Anonymous requests through the filter count as denials | 0005–0012 | applied to prompts |
| GOZ-0003-IF-08 | medium | No class exists for instance-wide, non-identity tables before GOZ-0012 | 0005–0011, 0023 | **human decision** (conditional; GOZ-0005 clarification) |
| GOZ-0003-IF-09 | medium | Telemetry and audit names; the broad `42501` → 404 mapping also masks grant defects | all | applied to prompts (optional narrowing noted) |
| GOZ-0003-IF-10 | medium | dave and Seeded Partner Org, seed semantics and key configuration. The GOZ-0012 "only one fixed org" bullet is stale | all, 0012 | applied to prompts |
| GOZ-0003-IF-11 | medium | Owner-run backup and restore fail under FORCE. Dumps contain the signing key. Rotation has no overlap window | 0039 | **human decision** (GOZ-0039 clarification) |
| GOZ-0003-IF-12 | medium | Catalog coverage gaps: definer functions outside `gozleme`; views, materialized views and foreign tables; policy content | 0003, 0013, 0015, 0017, 0039 | **code fix recommended** (drive-speckit); prompts updated |
| GOZ-0003-IF-13 | medium | Search, vector and caching under RLS: hashed-subplan filtering, ANN post-filter recall, unprotected external stores | 0013, 0015–0017, 0023 | applied to prompts |
| GOZ-0003-IF-14 | medium | GOZ-0040 and GOZ-0041 account tables must join `GlobalIdentity` and its exact-set tests | 0040, 0041 | applied to prompts |
| GOZ-0003-IF-15 | medium | Routing of GOZ-0003-OBS-01, the pre-existing GOZ-0001 re-sign-in session defect | 0001, 0040, 0041 | **human decision** |
| GOZ-0003-IF-16 | low | The context key is a high-value secret that sandboxes and secret scans must cover | 0037, 0038, 0039 | applied to prompts |
| GOZ-0003-IF-17 | low | research.md R5 and R11 differ from the code (`rls_internal` `PUBLIC` execute, verifier error trapping). The registry stage is stale | 0003 | **doc correction** (drive-speckit / Roadmap Steward) |
| GOZ-0003-IF-18 | low | Stale prompt references (`BootstrapBoundaryTests.cs:23`→`:24`, the "until GOZ-0003 merges" bullet, a renamed test) | 0012, all | applied to prompts |

Totals: 11 applied to prompts, 5 needing a human decision, 1 code fix recommended and 1 documentation correction. None is purely informational: every observation either changed a prompt or needs action.

## Prompt edits applied (within authority)

**All 38 prompts from GOZ-0004 to GOZ-0041** get a new section with provenance, `### Tenant RLS conventions (GOZ-0003-IF-01, IF-02, IF-09, IF-10, 2026-11-15)`. It covers:
- the tenant-table and function rules;
- the migration pins;
- the request transaction model, including the IF-03 and IF-04 "do not invent; decision pending" guards;
- telemetry and audit;
- fixtures and configuration.

**Spec-specific sections,** headed `### Implementation alignment (GOZ-0003-IF-…, 2026-11-15)`, were added to 29 prompts:

| Prompt | Findings |
|---|---|
| 0004 | IF-03 (hazard and pointer only), IF-05 |
| 0005 | IF-06, IF-07, IF-08 (pointer) |
| 0006, 0007, 0008 | IF-07, IF-08 (pointer) |
| 0009, 0010, 0011 | IF-02 (storage I/O and per-organization deduplication), IF-07, IF-08 (pointer) |
| 0012 | IF-06, IF-07 (defining shared/public; anonymous discovery), IF-10 (correction: second org exists), IF-18 (`:24`) |
| 0013 | IF-13 |
| 0014 | IF-06 |
| 0015, 0017 | IF-04 (pointer), IF-13 |
| 0016 | IF-02, IF-04 (pointer) |
| 0023 | IF-13 |
| 0024 | IF-06 |
| 0029, 0030, 0033, 0035, 0036 | IF-04 (pointer) |
| 0031, 0032 | IF-02, IF-04 (pointer) |
| 0034 | IF-04 (pointer), IF-06 |
| 0037, 0038 | IF-16 |
| 0039 | IF-11 (facts; role choice pending), IF-12, IF-16 |
| 0040, 0041 | IF-14, IF-15 (fact; routing pending) |

**Roadmap Steward:** bump `promptRevision` from 4 to 5 for GOZ-0004 to GOZ-0039, and from 2 to 3 for GOZ-0040 and GOZ-0041. Record GOZ-0003-IF-01 to IF-18 on the GOZ-0003 registry entry with the `impactAnalysis` gate and the evidence paths, and sync the GOZ-0003 stage and gates (IF-17). I did not edit the registry.

No outcome, scope, boundary, prerequisite, dependency, invariant or MVP commitment changed, and the DAG is unchanged. Where a finding would change one of them (IF-03, IF-04, IF-08, IF-11, IF-15), the prompts state only the verified fact and point to the pending decision. Earlier provenance bullets are left as written: new sections supersede them explicitly, as in the GOZ-0002 precedent. The edits were applied by a script and are idempotent: they are skipped if a `GOZ-0003-IF-` marker is already present. `make validate` passes after the edits (42 prompts, DAG, register in sync).

## Maintainer decisions needed

1. **GOZ-0003-IF-03: mechanism for cross-tenant data migrations.** Decide before the GOZ-0004 plan, whose initial-role rule depends on it. Options:
   - **(A)** A reviewed migration lifts the owner's binding inside its own transaction, using the transaction-local `gozleme.rls_helper` flag or `NO FORCE` and then `FORCE`. Transactional DDL means other sessions never see the window. A superuser test asserts the per-row results.
   - **(B)** DDL defaults only. Any per-row rule is deferred to application-time migration.
   - **(C)** A pinned, migration-only definer function for each backfill, added under security review and dropped afterwards.

   Each option amends the `force-rls` decision text, contract §5 and `local-development.md`.
2. **GOZ-0003-IF-04: tenant access for non-account principals.** Decide by the GOZ-0015 plan, or earlier if any spec from GOZ-0005 to GOZ-0014 adds background work. Options:
   - **(A)** A second signed token kind scoped to one organization and one purpose, with a new policy branch. It extends contract §1 and §3.
   - **(B)** The worker acts as the enqueuing account and re-verifies membership at processing time.
   - **(C)** A dedicated worker role with `BYPASSRLS`. This is an explicit exception to invariant 2, and needs a new role and `make reset`.
   - **(D)** Per-organization service accounts that are members of their organization. GOZ-0004 then needs a non-human member type.
3. **GOZ-0003-IF-08 (conditional): instance-wide non-identity tables before GOZ-0012.** Decide at GOZ-0005 clarification if such a table is needed. Options:
   - **(A)** Everything is tenant-owned by the publishing organization, and global uniqueness conflicts are accepted as revealed.
   - **(B)** A new fourth class with its own catalog rule.
   - **(C)** Bring GOZ-0012's classification work forward.
4. **GOZ-0003-IF-11: the role that runs backup and restore under FORCE RLS.** Decide at GOZ-0039 clarification. Options:
   - **(A)** A superuser, as an operator procedure.
   - **(B)** A dedicated operator-only `BYPASSRLS` role, `gozleme_backup`. It needs `01-roles.sh`, the Helm README and `make reset`.
   - **(C)** Physical, cluster-level backups only.
5. **GOZ-0003-IF-15: routing of GOZ-0003-OBS-01** (the GOZ-0001 re-sign-in session defect). Options:
   - **(A)** Recommended: a bounded `bugfix/goz-0001-resign-in-session` PR before GOZ-0040, with a non-incidental regression test.
   - **(B)** Fold the fix into GOZ-0040's scope.
   - **(C)** Accept it as a documented residual, and sign out before signing in from a signed-in client.

## Implementation defects and corrections for drive-speckit

I found no defect against a GOZ-0003 requirement or a confirmed architecture decision. I checked:
- the identity binding (signed account only, from the session principal);
- that the key is unreachable for `gozleme_app`;
- that context never survives commit, rollback or the pool;
- FORCE for both roles;
- the R4 amendment: the flag is honoured only when `current_user` is the owner, and the sequential-scan recursion test runs for both roles;
- the organization-then-invitation lock order through `lock_invited_organization`;
- existence-hiding parity for refused writes;
- the bounded telemetry labels;
- seed acting as a member;
- the Helm "do not cut a release" note being removed (FR-031).

Recommended bounded corrections, which do not block the PR:
- **IF-12 (code, recommended):**
  - extend `RlsCatalog` to flag migrator-owned `SECURITY DEFINER` functions in **every** non-system schema, not just `gozleme` and `gozleme_private`;
  - fail on unclassified `gozleme` relations of kind `v`, `m` or `f`;
  - add the matching negative cases in `RlsCatalogTests`.

  The R4 guard makes any such function an RLS bypass, and the migrator can create schemas, and functions in `public`.
- **IF-17 (documentation):** correct research.md R5 (`rls_internal` is not `PUBLIC`-executable, and the verifier uses regex prevalidation with no exception trapping) and R11 (`RlsCatalog` exempts no function from the `PUBLIC` check).
- **IF-09 (optional):** narrow `TenantContext.IsRefusal` to the row-level-security refusal, so that a missing `GRANT` surfaces as a 500 instead of a counted cross-tenant refusal.

If drive-speckit applies IF-12, I should re-analyse the changed catalog code.

## Not asserted

- I did not run the test suites or the demo. Results come from `evidence/demo-run.json` (`424a15e`) and from drive-speckit.
- I did not reproduce the IF-11 `pg_dump`/`COPY` behaviour under FORCE. It rests on documented PostgreSQL row-security semantics.
- I did not reproduce GOZ-0003-OBS-01. Its description comes from `progress.yaml`, and I confirmed only that the cited test signs in twice on an already signed-in client.
- I did not test the IF-12 claim that the migrator can create a definer function in `public` or a new schema. It rests on the migrator owning the database (it created `gozleme_private`) and on PostgreSQL 15+ ownership of `public` by `pg_database_owner`.
- I did not measure IF-13 planner behaviour at volume.
- Helm has still never been installed on a live cluster.
- This analysis did not push, merge, switch branches or edit the registry.
