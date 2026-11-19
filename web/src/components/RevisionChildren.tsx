import Link from "next/link";
import { childPage, kindLabel, NO_REVISION_CHILDREN, type RevisionChildList } from "@/lib/children";
import { revisionPage } from "@/lib/revisions";
import { CanonicalId } from "./CanonicalId";
import { DeclaredNotice } from "./DeclaredNotice";

/**
 * The **Children** section of a revision (GOZ-0008 User Story 1): its entries in projection order, each with kind,
 * native name, title and child ID, linked to the child's history, a page at a time (FR-013). No badge (FR-014).
 */
export function RevisionChildren({
  organizationId,
  publisherId,
  capabilityId,
  revisionId,
  list,
}: {
  organizationId: string;
  publisherId: string;
  capabilityId: string;
  revisionId: string;
  list: RevisionChildList;
}) {
  return (
    <section className="card" aria-labelledby="revision-children-heading" data-testid="revision-children">
      <h2 id="revision-children-heading">Children</h2>
      <DeclaredNotice />
      {list.items.length === 0 ? (
        <p className="muted" data-testid="no-revision-children">
          {NO_REVISION_CHILDREN}
        </p>
      ) : (
        <table className="table">
          <thead>
            <tr>
              <th scope="col">Kind</th>
              <th scope="col">Native name</th>
              <th scope="col">Title</th>
              <th scope="col">Child ID</th>
            </tr>
          </thead>
          <tbody>
            {list.items.map((c) => (
              <tr key={c.entryIndex} data-testid="revision-child-row" data-child-id={c.childId}>
                <td data-testid="revision-child-kind">{kindLabel(c.kind)}</td>
                <td className="cell-wrap">
                  <Link href={childPage(organizationId, publisherId, capabilityId, c.childId)} data-testid="revision-child-link">
                    <code data-testid="revision-child-name">{c.nativeName}</code>
                  </Link>
                  {c.duplicate && (
                    <span className="muted" data-testid="revision-child-duplicate">
                      {" "}
                      (duplicate entry)
                    </span>
                  )}
                </td>
                <td className="cell-wrap" data-testid="revision-child-title">
                  {c.title ?? <span className="muted">—</span>}
                </td>
                <td className="cell-wrap">
                  <CanonicalId value={c.canonicalId} label={`child ID of entry ${c.entryIndex + 1}`} testId="revision-child-canonical-id" />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {list.next !== null && (
        <p className="form-inline">
          <Link className="button" href={`${revisionPage(organizationId, publisherId, capabilityId, revisionId)}?after=${list.next}`} data-testid="revision-children-next">
            Next page
          </Link>
        </p>
      )}
    </section>
  );
}
