import Link from "next/link";
import type { Capability } from "@/lib/publishers";
import { revisionsPage } from "@/lib/revisions";
import { CanonicalId } from "./CanonicalId";
import { RenameDisplayNameForm } from "./RenameDisplayNameForm";
import { StateBadge } from "./StateBadge";

/**
 * A publisher's capabilities with their canonical ids and aliases (FR-013, FR-014). The rename form appears only
 * for callers holding `capability.rename` while the publisher is active, and **Validate manifest** for callers holding
 * `manifest.validate` (GOZ-0006). **Revisions** appears for callers holding `manifest.list`, also while the publisher
 * is suspended, because revisions stay readable (GOZ-0007); the API still decides every request.
 */
export function CapabilityList({
  organizationId,
  publisherId,
  capabilities,
  canRename,
  canValidate = false,
  canListRevisions = false,
}: {
  organizationId: string;
  publisherId: string;
  capabilities: Capability[];
  canRename: boolean;
  canValidate?: boolean;
  canListRevisions?: boolean;
}) {
  return (
    <section className="card" aria-labelledby="capabilities-heading">
      <h2 id="capabilities-heading">Capabilities</h2>
      {capabilities.length === 0 ? (
        <p className="muted">No capabilities yet.</p>
      ) : (
        <ul className="capability-list">
          {capabilities.map((c) => (
            <li key={c.id} className="capability" data-testid="capability-row" data-capability-id={c.id}>
              <div className="capability-head">
                <strong data-testid="capability-display-name">{c.displayName}</strong> <StateBadge state={c.state} />
              </div>
              <dl>
                <div className="row">
                  <dt>Canonical ID</dt>
                  <dd>
                    <CanonicalId value={c.canonicalId} label={`canonical ID of ${c.name}`} testId="capability-canonical-id" />
                  </dd>
                </div>
                <div className="row">
                  <dt>Alias</dt>
                  <dd>
                    <code data-testid="capability-alias">{c.alias}</code>
                  </dd>
                </div>
                <div className="row">
                  <dt>Name</dt>
                  <dd>
                    <code>{c.name}</code>
                  </dd>
                </div>
              </dl>
              {((canValidate && c.state === "active") || canListRevisions) && (
                <p className="form-inline detail-actions">
                  {canValidate && c.state === "active" && (
                    <Link
                      className="button"
                      href={`/organizations/${encodeURIComponent(organizationId)}/publishers/${encodeURIComponent(publisherId)}/capabilities/${encodeURIComponent(c.id)}/manifest`}
                      data-testid="validate-manifest-link"
                    >
                      Validate manifest
                    </Link>
                  )}
                  {canListRevisions && (
                    <Link className="button" href={revisionsPage(organizationId, publisherId, c.id)} data-testid="revisions-link">
                      Revisions
                    </Link>
                  )}
                </p>
              )}
              {canRename && c.state === "active" && (
                <RenameDisplayNameForm
                  key={c.displayName}
                  organizationId={organizationId}
                  path={`/api/organizations/${encodeURIComponent(organizationId)}/publishers/${encodeURIComponent(publisherId)}/capabilities/${encodeURIComponent(c.id)}`}
                  flow="capability_rename"
                  label={`Display name of ${c.name}`}
                  displayName={c.displayName}
                  inputId={`capability-rename-${c.id}`}
                />
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
