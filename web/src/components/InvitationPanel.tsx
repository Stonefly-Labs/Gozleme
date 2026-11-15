"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { formatUtc } from "@/lib/format";
import { send, unavailableText } from "@/lib/client";
import type { Invitation } from "@/lib/organizations";
import { traced } from "@/telemetry/ui";

/**
 * Single-use invitations (FR-007, FR-008, FR-012). A new code is shown exactly once, in this
 * component's memory only; the open list never shows codes. Any member may create or revoke.
 */
export function InvitationPanel({ organizationId, invitations }: { organizationId: string; invitations: Invitation[] }) {
  const router = useRouter();
  const [code, setCode] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const base = `/api/organizations/${encodeURIComponent(organizationId)}/invitations`;

  async function create() {
    setBusy(true);
    setError(null);
    const { status, json } = await traced("invite_create", () => send("POST", base), (r) => r.status === 201);
    setBusy(false);
    if (status === 201) {
      setCode((json as { code: string }).code);
      router.refresh();
    } else {
      setError(unavailableText);
    }
  }

  async function revoke(id: string) {
    setBusy(true);
    setError(null);
    const { status } = await traced("invite_revoke", () => send("DELETE", `${base}/${encodeURIComponent(id)}`), (r) => r.status === 204);
    setBusy(false);
    if (status !== 204) setError(status === 404 ? "That invitation is no longer open." : unavailableText);
    router.refresh();
  }

  return (
    <section className="card" aria-labelledby="invitations-heading">
      <h2 id="invitations-heading">Invitations</h2>
      <p className="form-alt">Create a code and share it with one person out of band. It works once and expires at the time shown below.</p>
      <button type="button" className="button button-primary" disabled={busy} onClick={create}>
        Create invitation
      </button>
      {code && (
        <div className="code-reveal" role="status">
          <label htmlFor="new-code">New invitation code — shown once, copy it now</label>
          <input id="new-code" data-testid="invitation-code" readOnly value={code} className="code-input" onFocus={(e) => e.currentTarget.select()} />
        </div>
      )}
      <h3>Open invitations</h3>
      {invitations.length === 0 ? (
        <p className="form-alt" data-testid="no-invitations">
          No open invitations.
        </p>
      ) : (
        <table className="table">
          <thead>
            <tr>
              <th scope="col">Created</th>
              <th scope="col">Expires</th>
              <th scope="col">
                <span className="visually-hidden">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {invitations.map((i) => (
              <tr key={i.id} data-testid="invitation-row">
                <td>{formatUtc(i.createdAt)}</td>
                <td>{formatUtc(i.expiresAt)}</td>
                <td className="cell-actions">
                  <button type="button" className="button" disabled={busy} onClick={() => revoke(i.id)} aria-label={`Revoke invitation created ${formatUtc(i.createdAt)}`}>
                    Revoke
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <p role="alert" className="form-error">
        {error}
      </p>
    </section>
  );
}
