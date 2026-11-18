import Link from "next/link";
import { formatUtc } from "@/lib/format";
import { type RevisionList as RevisionListData, revisionPage, revisionsPage, SUSPENDED_PUBLISHER } from "@/lib/revisions";
import { ShortDigest } from "./ShortDigest";
import { VerificationBadge } from "./VerificationBadge";

/**
 * A capability's revisions in publication order (GOZ-0007 User Story 1, R17): sequence, version, publication time,
 * the publishing account's opaque ID, the shortened native digest and the verification badge. Order is by
 * publication, not by version precedence, and no revision is labelled "latest". Paging follows the API's `next`.
 */
export function RevisionList({
  organizationId,
  publisherId,
  capabilityId,
  list,
}: {
  organizationId: string;
  publisherId: string;
  capabilityId: string;
  list: RevisionListData;
}) {
  return (
    <div className="stack">
      {list.publisherSuspended && (
        <div className="notice" role="status" data-testid="suspended-publisher-banner">
          <p>{SUSPENDED_PUBLISHER}</p>
        </div>
      )}
      <section className="card" aria-labelledby="revisions-heading" data-testid="revision-list">
        <h2 id="revisions-heading">Revisions</h2>
        {list.revisions.length === 0 ? (
          <p className="muted" data-testid="no-revisions">
            No revisions yet.
          </p>
        ) : (
          <table className="table">
            <thead>
              <tr>
                <th scope="col">#</th>
                <th scope="col">Version</th>
                <th scope="col">Published</th>
                <th scope="col">By account</th>
                <th scope="col">Native digest</th>
                <th scope="col">Verification</th>
              </tr>
            </thead>
            <tbody>
              {list.revisions.map((r) => (
                <tr key={r.id} data-testid="revision-row" data-revision-id={r.id}>
                  <td data-testid="revision-sequence">{r.sequence}</td>
                  <td>
                    <Link href={revisionPage(organizationId, publisherId, capabilityId, r.id)} data-testid="revision-link">
                      <code data-testid="revision-version">{r.version}</code>
                    </Link>
                  </td>
                  <td>
                    <time dateTime={r.publishedAt}>{formatUtc(r.publishedAt)}</time>
                  </td>
                  <td className="cell-wrap">
                    <code data-testid="revision-published-by">{r.publishedBy}</code>
                  </td>
                  <td>
                    <ShortDigest value={r.nativeDigest} label={`native digest of revision ${r.sequence}`} testId="revision-digest" />
                  </td>
                  <td>
                    <VerificationBadge verification={r.verification} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        {list.next !== null && (
          <p className="form-inline">
            <Link className="button" href={`${revisionsPage(organizationId, publisherId, capabilityId)}?after=${list.next}`} data-testid="revisions-next">
              Next page
            </Link>
          </p>
        )}
      </section>
    </div>
  );
}
