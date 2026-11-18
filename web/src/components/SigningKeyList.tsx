import { formatUtc } from "@/lib/format";
import { COMPROMISED_KEYS, NO_KEY_YET, publicKeyPath, type SigningKey, SUCCESSION_NOTE } from "@/lib/revisions";

/**
 * A publisher's registry-held signing keys by generation (GOZ-0007 contracts/signing-keys.md "Key view"): key ID,
 * algorithm, creation time, active or retired with time, reason and successor. Public parts only; succession is a
 * local operator action with no control here.
 */
export function SigningKeyList({ organizationId, publisherId, keys }: { organizationId: string; publisherId: string; keys: SigningKey[] }) {
  const generationOf = new Map(keys.map((k) => [k.id, k.generation]));
  return (
    <section className="card" aria-labelledby="keys-heading" data-testid="signing-key-list">
      <h2 id="keys-heading">Signing keys</h2>
      {keys.some((k) => k.retiredReason === "compromised") && (
        <div className="notice notice-danger" role="alert" data-testid="compromised-banner">
          <p>{COMPROMISED_KEYS}</p>
        </div>
      )}
      <p className="field-hint" data-testid="succession-note">
        {SUCCESSION_NOTE}
      </p>
      {keys.length === 0 ? (
        <p className="muted" data-testid="no-key-yet">
          {NO_KEY_YET}
        </p>
      ) : (
        <table className="table">
          <thead>
            <tr>
              <th scope="col">Generation</th>
              <th scope="col">Key ID</th>
              <th scope="col">Algorithm</th>
              <th scope="col">Created</th>
              <th scope="col">Status</th>
              <th scope="col">Public key</th>
            </tr>
          </thead>
          <tbody>
            {keys.map((k) => (
              <tr key={k.id} data-testid="signing-key-row" data-key-status={k.status}>
                <td>{k.generation}</td>
                <td className="cell-wrap">
                  <code data-testid="signing-key-id">{k.keyId}</code>
                </td>
                <td>
                  <code>{k.algorithm}</code>
                </td>
                <td>
                  <time dateTime={k.createdAt}>{formatUtc(k.createdAt)}</time>
                </td>
                <td data-testid="signing-key-status">
                  {k.status === "active" ? (
                    <span className="badge badge-ok">Active</span>
                  ) : (
                    <>
                      <span className={k.retiredReason === "compromised" ? "badge badge-down" : "badge badge-warn"}>
                        {k.retiredReason === "compromised" ? "Retired: compromised" : "Retired: lost"}
                      </span>
                      {k.retiredAt && (
                        <>
                          {" "}
                          <time dateTime={k.retiredAt}>{formatUtc(k.retiredAt)}</time>
                        </>
                      )}
                      {k.successorId && (
                        <span className="muted" data-testid="signing-key-successor">
                          {" "}
                          · successor: generation {generationOf.get(k.successorId) ?? "?"}
                        </span>
                      )}
                    </>
                  )}
                </td>
                <td>
                  <a href={publicKeyPath(organizationId, publisherId, k.id)} download="public-key.pem">
                    PEM
                  </a>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
