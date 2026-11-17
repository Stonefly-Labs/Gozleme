import Link from "next/link";
import type { Publisher } from "@/lib/publishers";
import { CanonicalId } from "./CanonicalId";
import { StateBadge } from "./StateBadge";

/** The organization's publishers (member-level read, FR-023). Ids come from the API and are never derived here. */
export function PublisherList({ organizationId, publishers }: { organizationId: string; publishers: Publisher[] }) {
  return (
    <section className="card" aria-labelledby="publishers-heading">
      <h2 id="publishers-heading">Publishers</h2>
      {publishers.length === 0 ? (
        <p className="muted">No publishers yet.</p>
      ) : (
        <table className="table">
          <thead>
            <tr>
              <th scope="col">Handle</th>
              <th scope="col">Name</th>
              <th scope="col">Publisher ID</th>
              <th scope="col">State</th>
            </tr>
          </thead>
          <tbody>
            {publishers.map((p) => (
              <tr key={p.id} data-testid="publisher-row">
                <td className="cell-wrap">
                  <Link href={`/organizations/${encodeURIComponent(organizationId)}/publishers/${encodeURIComponent(p.id)}`}>
                    <code>{p.handle}</code>
                  </Link>
                </td>
                <td className="cell-wrap">{p.displayName}</td>
                <td className="cell-wrap">
                  <CanonicalId value={p.publisherId} label={`publisher ID of ${p.handle}`} />
                </td>
                <td>
                  <StateBadge state={p.state} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
