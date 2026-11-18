import { formatUtc } from "@/lib/format";
import {
  COMPROMISED_KEY,
  DOWNLOAD_PARTS,
  type DownloadPart,
  downloadPath,
  publicKeyPath,
  type RevisionDetail,
} from "@/lib/revisions";
import { VerificationBadge } from "./VerificationBadge";

const DOWNLOAD_LABELS: Record<DownloadPart, string> = {
  native: "Native document",
  projection: "Projection (canonical JSON)",
  statement: "Signed statement (canonical JSON)",
  signature: "Signature (64 raw bytes)",
};

/** Statement members as `a.b` paths with their values, in the statement's own (sorted) order. */
function fields(value: unknown, prefix = ""): [string, string][] {
  if (value !== null && typeof value === "object" && !Array.isArray(value)) {
    return Object.entries(value as Record<string, unknown>).flatMap(([k, v]) => fields(v, prefix ? `${prefix}.${k}` : k));
  }
  return [[prefix, typeof value === "string" ? value : JSON.stringify(value)]];
}

/**
 * The attestation of one revision (GOZ-0007 User Story 3, R17): the signed statement's fields, key ID, algorithm,
 * statement version, the verification result computed now, the key's status (a prominent banner when it was retired
 * as compromised), the fixed wording of what the attestation proves, and the downloads for independent checking.
 */
export function AttestationPanel({
  organizationId,
  publisherId,
  capabilityId,
  revision,
}: {
  organizationId: string;
  publisherId: string;
  capabilityId: string;
  revision: RevisionDetail;
}) {
  const { attestation, verification } = revision;
  const key = verification.key;
  const statement = fields(attestation.statement);
  return (
    <section className="card" aria-labelledby="attestation-heading" data-testid="panel-attestation">
      <div className="panel-head">
        <h2 id="attestation-heading">Attestation</h2>
        <span className="panel-version" data-testid="attestation-statement-version">{`statement v${attestation.statementVersion}`}</span>
      </div>
      {key.status === "retired" && key.retiredReason === "compromised" && (
        <div className="notice notice-danger" role="alert" data-testid="compromised-banner">
          <p>{COMPROMISED_KEY}</p>
        </div>
      )}
      <p className="proof" data-testid="attestation-proof">
        {attestation.proof}
      </p>
      <dl>
        <div className="row">
          <dt>Verification</dt>
          <dd>
            <VerificationBadge verification={verification} detailed />
          </dd>
        </div>
        <div className="row">
          <dt>Algorithm</dt>
          <dd>
            <code data-testid="attestation-algorithm">{attestation.algorithm}</code>
          </dd>
        </div>
        <div className="row">
          <dt>Key ID</dt>
          <dd>
            <code className="cell-wrap" data-testid="attestation-key-id">
              {attestation.keyId}
            </code>
          </dd>
        </div>
        <div className="row">
          <dt>Key status</dt>
          <dd data-testid="key-status">
            {key.status === "active" ? (
              <span className="badge badge-ok">Active</span>
            ) : (
              <>
                <span className="badge badge-warn">Retired</span>{" "}
                {key.retiredReason && <code data-testid="key-retired-reason">{key.retiredReason}</code>}{" "}
                {key.retiredAt && <time dateTime={key.retiredAt}>{formatUtc(key.retiredAt)}</time>}
              </>
            )}
          </dd>
        </div>
        <div className="row">
          <dt>Public key</dt>
          <dd>
            <a href={publicKeyPath(organizationId, publisherId, key.id)} download="public-key.pem" data-testid="download-public-key">
              Download public key (PEM)
            </a>
          </dd>
        </div>
      </dl>
      <h3>Signed statement</h3>
      {statement.length === 0 ? (
        <p className="muted" data-testid="statement-unreadable">
          The stored statement could not be read.
        </p>
      ) : (
        <table className="table" data-testid="statement-fields">
          <thead>
            <tr>
              <th scope="col">Field</th>
              <th scope="col">Value</th>
            </tr>
          </thead>
          <tbody>
            {statement.map(([name, value]) => (
              <tr key={name} data-testid={`statement-${name}`}>
                <td>
                  <code>{name}</code>
                </td>
                <td className="cell-wrap">
                  <code>{value}</code>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <h3>Downloads</h3>
      <ul className="download-list" data-testid="downloads">
        {DOWNLOAD_PARTS.map((part) => (
          <li key={part}>
            <a href={downloadPath(organizationId, publisherId, capabilityId, revision.id, part)} download data-testid={`download-${part}`}>
              {DOWNLOAD_LABELS[part]}
            </a>
          </li>
        ))}
      </ul>
    </section>
  );
}
