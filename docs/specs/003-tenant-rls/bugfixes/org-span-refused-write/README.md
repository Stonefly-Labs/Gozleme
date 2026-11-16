# Organization span after a write refused by row-level security

**Found:** 2026-11-16, while fixing the flaky telemetry tests (PR #13). **Fix requested by the
maintainer:** 2026-11-16.

## Defect

When row-level security refused a write (SQLSTATE `42501`), the error unwound through the organization
handler, which ended its `gozleme.org.*` span with no `gozleme.org.outcome` or
`gozleme.org.failure_category`. Only then did `TenantTransaction` answer "not found" and count
`refused_write` on the tenant span. That broke GOZ-0002's bounded span contract (research R11): every
organization span carries an outcome and a bounded failure category.

## Fix

`RefusedWriteSpanInterceptor` (`src/Gozleme.Api/Organizations/RefusedWriteSpanInterceptor.cs`) is an EF
command interceptor. When a command fails with a refusal, it closes the enclosing, still-open
organization span as `outcome=failure`, `failure_category=refused_write`, and counts it on
`gozleme.org.operations`. It adds no organization ID, and it never overwrites an outcome the handler
already recorded. The category list in GOZ-0002 research R11 and the GOZ-0003 telemetry contract (§7)
are amended.

## Evidence

- **Integration test**, failing before the fix (`outcome` was `null`) and passing after it:
  `TenantTelemetryTests.A_refused_write_ends_its_organization_span_with_a_bounded_failure_and_counts_it`.
  The full .NET suite passes, 266/266.
- **Cumulative demo** (`make demo SPEC=GOZ-0003`): fresh stack, clean tree at `63caf08`, 33/33 passed.
  The run record is in [`demo-run.json`](demo-run.json). The fail-closed step now creates an
  organization from the browser under a mismatched key, expects `404`, and asserts that Prometheus shows
  `gozleme_org_operations_total{gozleme_org_operation="create", gozleme_org_failure_category="refused_write"}`.
  This is not a visual change, so there are no new screenshots. GOZ-0003's merged evidence was restored
  afterwards and is unchanged.
