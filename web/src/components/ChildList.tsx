import Link from "next/link";
import { type ChildList as ChildListData, childPage, childrenPage, kindLabel, NO_CHILDREN } from "@/lib/children";
import { revisionPage } from "@/lib/revisions";
import { CanonicalId } from "./CanonicalId";
import { DeclaredNotice } from "./DeclaredNotice";

/**
 * A capability's children in creation order (GOZ-0008 User Story 1): kind, child ID (linked to its history), and the
 * native name and title in the latest revision that declares it, and how many revisions it appears in (linked to the
 * child's history, which lists them). Children are declared, not verified, so no approval,
 * trust, safety, lifecycle or verification badge is shown (FR-014).
 */
export function ChildList({ organizationId, publisherId, capabilityId, list }: { organizationId: string; publisherId: string; capabilityId: string; list: ChildListData }) {
  return (
    <section className="card" aria-labelledby="children-heading" data-testid="child-list">
      <h2 id="children-heading">Children</h2>
      <DeclaredNotice />
      {list.items.length === 0 ? (
        <p className="muted" data-testid="no-children">
          {NO_CHILDREN}
        </p>
      ) : (
        <table className="table">
          <thead>
            <tr>
              <th scope="col">Kind</th>
              <th scope="col">Native name</th>
              <th scope="col">Title</th>
              <th scope="col">Child ID</th>
              <th scope="col">Appears in</th>
              <th scope="col">Last declared in</th>
            </tr>
          </thead>
          <tbody>
            {list.items.map((c) => (
              <tr key={c.id} data-testid="child-row" data-child-id={c.id}>
                <td data-testid="child-kind">{kindLabel(c.kind)}</td>
                <td className="cell-wrap">
                  <Link href={childPage(organizationId, publisherId, capabilityId, c.id)} data-testid="child-link">
                    <code data-testid="child-name">{c.latest.nativeName}</code>
                  </Link>
                </td>
                <td className="cell-wrap" data-testid="child-title">
                  {c.latest.title ?? <span className="muted">—</span>}
                </td>
                <td className="cell-wrap">
                  <CanonicalId value={c.canonicalId} label={`child ID ${c.ordinal}`} testId="child-canonical-id" />
                </td>
                <td>
                  <Link href={childPage(organizationId, publisherId, capabilityId, c.id)} data-testid="child-present-in">
                    {c.presentIn} {c.presentIn === 1 ? "revision" : "revisions"}
                  </Link>
                </td>
                <td>
                  <Link href={revisionPage(organizationId, publisherId, capabilityId, c.latest.revisionId)} data-testid="child-latest">
                    #{c.latest.sequence} <code>{c.latest.version}</code>
                  </Link>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {list.next !== null && (
        <p className="form-inline">
          <Link className="button" href={`${childrenPage(organizationId, publisherId, capabilityId)}?after=${list.next}`} data-testid="children-next">
            Next page
          </Link>
        </p>
      )}
    </section>
  );
}
