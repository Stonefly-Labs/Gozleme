import Link from "next/link";
import { type ChildDetail as ChildDetailData, childPage, DUPLICATE_ENTRY, kindLabel } from "@/lib/children";
import { revisionPage } from "@/lib/revisions";
import { CanonicalId } from "./CanonicalId";
import { DeclaredNotice } from "./DeclaredNotice";

/**
 * One child (GOZ-0008 User Stories 1, 2 and 5): kind, child ID, parent capability, and its history over every revision
 * of the capability in publication order: **Present** with the native name and title there, or **Absent**. A
 * duplicate child (FR-016a) carries the fixed note. No status beyond present or absent, and no badge (FR-014).
 */
export function ChildDetail({ organizationId, publisherId, capabilityId, detail }: { organizationId: string; publisherId: string; capabilityId: string; detail: ChildDetailData }) {
  const { child, capability } = detail;
  return (
    <div className="stack">
      <section className="card" aria-labelledby="child-heading" data-testid="child-detail">
        <h2 id="child-heading">
          {kindLabel(child.kind)} <code data-testid="child-name">{child.latest.nativeName}</code>
        </h2>
        <DeclaredNotice />
        {child.duplicate && (
          <p className="note" data-testid="duplicate-note">
            {DUPLICATE_ENTRY}
          </p>
        )}
        <dl>
          <div className="row">
            <dt>Kind</dt>
            <dd data-testid="child-kind">{kindLabel(child.kind)}</dd>
          </div>
          <div className="row">
            <dt>Child ID</dt>
            <dd>
              <CanonicalId value={child.canonicalId} label="child ID" testId="child-canonical-id" />
            </dd>
          </div>
          <div className="row">
            <dt>Capability</dt>
            <dd>
              <span data-testid="child-capability-name">{capability.displayName}</span>{" "}
              <CanonicalId value={capability.canonicalId} label="capability ID" testId="child-capability-id" />
            </dd>
          </div>
        </dl>
      </section>
      <section className="card" aria-labelledby="history-heading" data-testid="child-history">
        <h2 id="history-heading">History</h2>
        <table className="table">
          <thead>
            <tr>
              <th scope="col">#</th>
              <th scope="col">Version</th>
              <th scope="col">In this revision</th>
              <th scope="col">Native name</th>
              <th scope="col">Title</th>
            </tr>
          </thead>
          <tbody>
            {detail.history.map((h) => (
              <tr key={h.revisionId} data-testid="history-row" data-present={h.present}>
                <td data-testid="history-sequence">{h.sequence}</td>
                <td>
                  <Link href={revisionPage(organizationId, publisherId, capabilityId, h.revisionId)}>
                    <code data-testid="history-version">{h.version}</code>
                  </Link>
                </td>
                <td data-testid="history-presence">{h.present ? "Present" : "Absent"}</td>
                <td className="cell-wrap">{h.nativeName !== null ? <code data-testid="history-name">{h.nativeName}</code> : <span className="muted">—</span>}</td>
                <td className="cell-wrap" data-testid="history-title">
                  {h.title ?? <span className="muted">—</span>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {detail.next !== null && (
          <p className="form-inline">
            <Link className="button" href={`${childPage(organizationId, publisherId, capabilityId, child.id)}?after=${detail.next}`} data-testid="history-next">
              Next page
            </Link>
          </p>
        )}
      </section>
    </div>
  );
}
