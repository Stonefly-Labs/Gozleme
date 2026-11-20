import { ATTESTED, COMPARISON_LABELS, GENERATIONS_NOTE, type GenerationList, generationDownloadPath, NOT_ATTESTED } from "@/lib/children";
import { formatUtc } from "@/lib/format";
import { ShortDigest } from "./ShortDigest";

/**
 * The **Projection generations** section of a revision (GOZ-0008 User Story 3): generation 1 is the attested
 * publication projection; later generations come from the operator command and are labelled **Not attested**
 * (FR-011). Each has its own generation ID, never a child ID, and a download of its canonical bytes.
 */
export function ProjectionGenerations({
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
  list: GenerationList;
}) {
  return (
    <section className="card" aria-labelledby="generations-heading" data-testid="projection-generations">
      <h2 id="generations-heading">Projection generations</h2>
      <p className="field-hint">{GENERATIONS_NOTE}</p>
      <table className="table">
        <thead>
          <tr>
            <th scope="col">Generation</th>
            <th scope="col">Generation ID</th>
            <th scope="col">Adapter</th>
            <th scope="col">Digest</th>
            <th scope="col">Recorded</th>
            <th scope="col">Attestation</th>
            <th scope="col">Download</th>
          </tr>
        </thead>
        <tbody>
          {list.items.map((g) => (
            <tr key={g.id} data-testid="generation-row" data-generation={g.generation}>
              <td data-testid="generation-number">{g.generation}</td>
              <td className="cell-wrap">
                <code data-testid="generation-id">{g.id}</code>
              </td>
              <td>
                <code data-testid="generation-adapter">
                  {g.adapter.id}/{g.adapter.version}
                </code>{" "}
                <span className="muted">projection {g.projectionVersion}</span>
              </td>
              <td>
                <ShortDigest value={g.digest} label={`projection digest of generation ${g.generation}`} testId="generation-digest" />
                {g.comparedToPrevious && (
                  <p className="field-hint" data-testid="generation-comparison">
                    {COMPARISON_LABELS[g.comparedToPrevious]}
                  </p>
                )}
              </td>
              <td>
                <time dateTime={g.createdAt}>{formatUtc(g.createdAt)}</time>
              </td>
              <td data-testid="generation-attested">{g.attested ? ATTESTED : NOT_ATTESTED}</td>
              <td>
                <a href={generationDownloadPath(organizationId, publisherId, capabilityId, revisionId, g.id)} download data-testid="generation-download">
                  projection-generation-{g.generation}.json
                </a>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
