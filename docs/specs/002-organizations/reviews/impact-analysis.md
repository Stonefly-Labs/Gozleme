# GOZ-0002 post-convergence spec impact analysis

**Source spec:** GOZ-0002, Organizations and membership
**Implementation revision:** analysed at `f95be4c` on `feature/goz-0002-organizations`. The demo ran on a clean tree at `8a9941e`: 29 of 29 recorded assertions passed, with the GOZ-0000 and GOZ-0001 suites replayed. `git diff 8a9941e f95be4c` touches only `evidence/`, `progress.yaml` and `tasks.md`, so the demo evidence still applies to the code.
**Analyst:** Spec Impact Analyst (`analyze-spec-impact`), 2026-11-15
**Structured findings:** `docs/specs/002-organizations/reviews/impact-findings.json` (19 findings; validates against `.gozleme/schemas/impact-findings.schema.json`)

## Scope inspected

- **API code:** `src/Gozleme.Api`:
  - `Organizations/*`: entities, options, contracts, endpoints, access check, invitation codes, display names, telemetry, and the redeem rate-limit policy;
  - `Identity/SecurityAudit.cs` (events 1101–1108), `Identity/IdentityServiceCollectionExtensions.cs` (`RateLimitedOperation`, `OnRejected`), `Identity/OperatorCommands.cs` (carol, Seeded Demo Org);
  - `GozlemeApp.cs` (middleware order), `Persistence/GozlemeDbContext.cs` and migration `20261116000000_Organizations`.
- **Web code:** `web/src/lib/organizations.ts`, `lib/client.ts`, the organization pages, `/join`, `SignedInHeader`, `OrganizationSwitcher`, and the route handlers under `app/api/organizations` and `app/api/invitations`.
- **Deployment:** `deploy/helm/gozleme` (values and api template), `docs/engineering/local-development.md`.
- **Tests and demo:**
  - integration tests: organizations, invitations, membership, isolation, boundary, telemetry, plus the narrowed bootstrap boundary;
  - `tests/Gozleme.Api.Tests/OrganizationPolicyTests.cs`;
  - `e2e/playwright.config.ts`, `e2e/tests/{organizations-demo.spec.ts,evidence.ts,telemetry.ts,bootstrap-demo.spec.ts}`;
  - `evidence/demo-run.json` and the masked invitation screenshot.
- **Spec workspace:**
  - spec.md (Clarifications, FR-001 to FR-027) and plan.md (including Complexity Tracking);
  - research.md R1 to R14 and data-model.md;
  - contracts/organizations-api.openapi.yaml and contracts/web-routes.md;
  - tasks.md and progress.yaml.
- **Governance:** `security-invariants.md`, `decision-register.md` (R3-08, R3-19, R3-24), `.gozleme/spec-registry.yaml`.
- **Downstream prompts:** every prompt from GOZ-0003 to GOZ-0041. All 39 depend on GOZ-0002 transitively (GOZ-0040 directly, GOZ-0041 through GOZ-0040). I read each prompt's outcome, scope, boundary and earlier alignment sections against the code.

## Verified implementation facts that downstream specs consume

| Area | Fact at `f95be4c` (evidence) |
|---|---|
| Tables | `gozleme.organizations` (`id` UUIDv7, `display_name` varchar(100) **not unique**, `created_by` SET NULL), `memberships` (PK `organization_id, user_id`; `joined_via` creator/invitation; `invitation_id`; `selected_at`), `invitations` (`code_hash` bytea(32) unique, expiry, used/revoked pairs). Every table has an `organization_id` key. **No RLS** (migration; plan Complexity Tracking). No role, owner or admin column (`OrganizationBoundaryTests.cs:24-38`). |
| Authority | Interim role-neutral rule: any member renames, invites, lists and revokes invitations, lists members, removes others, or leaves. The last member is guarded under an `organizations` row `FOR UPDATE` lock (`OrganizationEndpoints.cs:167-198`). |
| Access check | `OrganizationAccess.FindMembershipAsync` re-reads membership on every request. Malformed, unknown and non-member IDs all get the same empty 404 with `no-store` (`OrganizationAccess.cs:17-25`; `OrganizationIsolationTests`). |
| Context | `memberships.selected_at` is a preference: `activeOrganizationId` is last selected, else last joined, else null (`OrganizationEndpoints.cs:58-63`). It is never a session claim, and the security stamp is not rotated. |
| Invitations | 160-bit Crockford base32 codes (32 characters in groups of 4), normalised. Only the SHA-256 hash is stored. Expiry is 7 days (1–720 h). Codes are single use, enforced by a `FOR UPDATE` lock. One generic `400 invalid_invitation`. An existing member does not consume a code. Redemption is limited to 10 per minute per account, in memory and per instance (`InvitationCodes.cs`, `OrganizationServiceCollectionExtensions.cs`). |
| Boundaries | No organization deletion, ownership transfer, add-by-email, roles or delegation (`OrganizationBoundaryTests`; e2e boundary test). `/api/v1/users` remains 404. |
| Telemetry | Spans `gozleme.org.<op>` for 8 operations, and the counter `gozleme.org.operations{operation,outcome,failure_category}`. `gozleme.organization.id` is a span attribute only. Audit events 1101–1108 carry opaque IDs. The demo checks that no emails, names or codes appear in traces, metrics or logs. |
| Web | Pages `/organizations`, `/organizations/[id]` and `/join` each gate with `getSession()`. A header switcher and links appear on signed-in pages. 8 route handlers, each unsafe one checked with `isSameOrigin()`. `segment()` neutralises dot segments. |
| Seed and demo | `seed` adds carol and Seeded Demo Org `0193a000-0000-7000-8000-000000000002` (alice and carol, no invitations), restored on every run. The `goz-0002` project depends on `goz-0001`. Run-unique organizations accumulate for alice, and bob ends with none. `recentSpans` gains an `operation` filter. |

## Findings

| ID | Sev | Finding | Affected specs | Disposition |
|---|---|---|---|---|
| GOZ-0002-IF-01 | high | Tenant-owned tables exist without RLS: an interim exception to invariant 1 | 0003, 0039 (all tenant-data specs) | **human-decision** (acknowledge); prompts updated |
| GOZ-0002-IF-02 | high | Access paths that cross tenants before any tenant is known (list, membership check, redeem, create, seed) shape the RLS design | 0003 | applied to prompts |
| GOZ-0002-IF-03 | high | GOZ-0004 must replace the role-neutral rule; the initial-role migration for existing members needs the maintainer | 0004, 0014 | applied to prompts (decision routed to GOZ-0004 clarify) |
| GOZ-0002-IF-04 | medium | Removing a member does not revoke that member's open invitations; a removed account can rejoin with its own code | 0002, 0004 | **human-decision**: fix now (recommended) or defer |
| GOZ-0002-IF-05 | medium | Conventions for organization-scoped APIs, pages and existence hiding | 0003–0041 | applied to prompts |
| GOZ-0002-IF-06 | medium | New fixtures (carol, Seeded Demo Org) and leftover demo state; a second seeded organization is needed for two-tenant demos | 0003–0041, 0012 | applied to prompts |
| GOZ-0002-IF-07 | medium | Organization ID on spans, per-module audit event ID ranges, membership history only in logs | 0003, 0023, all | applied to prompts |
| GOZ-0002-IF-08 | medium | A rate-limited endpoint without `RateLimitedOperation` is mislabelled as an auth sign-in rejection | 0009, 0013, 0015, 0038, 0040, 0041, all | applied to prompts |
| GOZ-0002-IF-09 | low | Narrowed boundary assertions and stale line references | 0004, 0005, 0007, 0012, 0013 | applied to prompts (stale references fixed) |
| GOZ-0002-IF-10 | medium | Display names are not unique and the UI shows only names | 0005, 0014, 0020, 0034 (0002 polish) | applied to prompts |
| GOZ-0002-IF-11 | low | Organization IDs are local and are not federated identities | 0005, 0029, 0034 | applied to prompts |
| GOZ-0002-IF-12 | low | Foreign-key, retention and backup facts; account-deletion cascade would bypass the last-member guard | 0003, 0014, 0035, 0039 | applied to prompts |
| GOZ-0002-IF-13 | low | No quotas on organization or invitation creation, and no paging | 0004, 0039 | applied to prompts |
| GOZ-0002-IF-14 | low | Joining is only by creation or code; email and OIDC must not add join paths without approval | 0003, 0004, 0040, 0041 | applied to prompts |
| GOZ-0002-IF-15 | low | Role-neutral invitations allow one person to bring several accounts; separation of duties applies between accounts | 0004, 0021 | applied to prompts |
| GOZ-0002-IF-16 | low | Organizations are flat; the policy subject is a membership with no role | 0018, 0020 | applied to prompts |
| GOZ-0002-IF-17 | low | Planning documents differ from the code (endpoint count, test file names, R11 wording, spec status) | 0002 | **code-fix-needed** (docs only) |
| GOZ-0002-IF-18 | low | Helm does not expose `Gozleme:Organizations:*`, although the spec says an operator can configure them | 0002, 0039 | **code-fix-needed** (Helm values, template, README) |
| GOZ-0002-IF-19 | medium | Later caches must invalidate on membership change, because removal relies on no caching | 0023 | applied to prompts |

The evidence (file:line), recommendation and affected specs for each finding are in `impact-findings.json`.

## Prompt edits applied (within authority)

**All 39 future prompts, GOZ-0003 to GOZ-0041,** get a new section, `### Organization and tenancy conventions (GOZ-0002-IF-01, IF-05, IF-06, IF-07, IF-08, 2026-11-15)`, with provenance. It covers:
- the tenancy model and the organization-scoped API and page convention;
- the fact that isolation is application-layer only until GOZ-0003;
- the telemetry and audit precedent, including per-module audit event ID ranges;
- `RateLimitedOperation` labelling;
- seeded fixtures, leftover demo state and the e2e telemetry helpers.

**Spec-specific sections,** headed `### Implementation alignment (GOZ-0002-IF-…, 2026-11-15)`, were added to 17 prompts:

| Prompt | Findings |
|---|---|
| 0003 | IF-01, 02, 07, 12, 14 |
| 0004 | IF-03, 04, 09, 13, 14, 15 |
| 0005 | IF-09, 10, 11 |
| 0007 | IF-09 |
| 0012 | IF-05, 06, 09 |
| 0013 | IF-09 |
| 0014 | IF-03, 10, 12 |
| 0018 | IF-16 |
| 0020 | IF-10, 16 |
| 0021 | IF-15 |
| 0023 | IF-07, 19 |
| 0029 | IF-11 |
| 0034 | IF-10, 11 |
| 0035 | IF-12 |
| 0039 | IF-01, 12, 13, 18 |
| 0040 | IF-14 (supersedes its pre-implementation "GOZ-0002: invitations" downstream reference) |
| 0041 | IF-14 |

**In-place corrections:** the stale GOZ-0001-IF-10 line references in `spec-0005.md` (`BootstrapBoundaryTests.cs:24`) and `spec-0012.md` (`:23`, `bootstrap-demo.spec.ts:132,141-142`) are marked "refreshed by GOZ-0002-IF-09".

**Roadmap Steward:** bump `promptRevision` from 3 to 4 for GOZ-0003 to GOZ-0039, and from 1 to 2 for GOZ-0040 and GOZ-0041. Record GOZ-0002-IF-01 to IF-19 on the GOZ-0002 registry entry, along with the `impactAnalysis` gate and the evidence paths. I did not edit the registry.

No outcome, scope, boundary, prerequisite, dependency, invariant or MVP commitment changed, and the DAG is unchanged. The edits were applied by a script and are idempotent: they are skipped if a `GOZ-0002-IF-` marker is already present. `make validate` passes after the edits (42 prompts, DAG, register in sync).

## Items requiring human decision

1. **GOZ-0002-IF-01: acknowledge the interim invariant-1 exception.** GOZ-0002 creates tenant-owned rows protected only by application authorization. The plan records this under Complexity Tracking, citing the approved roadmap order, but invariant 1 is a confirmed security guarantee, so the exception needs explicit maintainer acknowledgement at PR review. Proposed conditions:
   - GOZ-0003 is the next spec to add or touch tenant-owned data. GOZ-0040 adds only global identity tables, so it may still go first.
   - No release or tag is cut while the exception is open.
   - The GOZ-0039 gate verifies effective RLS on every tenant-owned table.
2. **GOZ-0002-IF-04: removal and a removed member's own open invitations.** Choose one:
   - **(a) Bounded GOZ-0002 correction (recommended).** Remove and leave also revoke the departing account's open invitations, in the same transaction, with audit 1104 and a test. Then re-analyse.
   - **(b) Interim residual.** Accept it for now and let GOZ-0004 close it. Its prompt already requires that.
3. **Routed, not decided now:**
   - **GOZ-0002-IF-03:** how existing members get their initial roles when GOZ-0004 lands. The GOZ-0004 prompt requires this to be decided with the maintainer during clarification.
   - **GOZ-0002-IF-07:** whether membership events join the durable audit record. GOZ-0023 decides, carrying GOZ-0001-IF-11.

## Implementation defects and corrections for drive-speckit

I found no defect that violates a GOZ-0002 requirement or a confirmed architecture decision. I checked:
- existence-hiding parity, including sub-resource IDs and the absence of a route constraint;
- the last-member guard under concurrent removal (lock, then re-read);
- single-use redemption (`FOR UPDATE` plus the PK backstop);
- the already-member path, which does not consume the code;
- that no context is kept as a session claim and the stamp is not rotated;
- middleware order: authentication runs before the rate limiter, so the redeem partition is the account;
- the cross-site layers on every unsafe route;
- no names, emails or codes in telemetry, logs or URLs;
- masking of the invitation code in screenshot 05.

Bounded corrections to consider before the PR:
- **IF-04:** a security gap the spec does not cover. This is the maintainer's choice above.
- **IF-17:** documentation drift in plan.md, tasks.md T013, research.md R11 and the spec status.
- **IF-18:** Helm values for the organization options.
- **IF-10 (optional UI polish):** disambiguate organizations whose names collide in the switcher and list.

If any code changes, re-run the demo and re-analyse.

## Not asserted

- I did not run the test suites or the demo again. Demo results come from `evidence/demo-run.json`, and test results come from the `progress.yaml` gates (202 .NET tests and 131 web Vitest tests).
- I did not review the concurrency tests' timing robustness beyond reading their structure.
- Helm has never been installed on a live cluster.
- This analysis did not commit, push, merge, switch branches or edit the registry.
