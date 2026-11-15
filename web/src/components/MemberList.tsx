"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { formatUtc } from "@/lib/format";
import { send, unavailableText } from "@/lib/client";
import type { Member } from "@/lib/organizations";
import { traced } from "@/telemetry/ui";

export const LAST_MEMBER = "An organization must keep at least one member.";

/**
 * Members (FR-014–FR-016). Interim role-neutral rule until GOZ-0004: any member may remove any other
 * member and anyone may leave; the last member can do neither. There are no role controls.
 */
export function MemberList({ organizationId, members }: { organizationId: string; members: Member[] }) {
  const router = useRouter();
  const [confirming, setConfirming] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  async function remove(member: Member) {
    setBusy(true);
    setMessage(null);
    const { status } = await traced(
      member.isSelf ? "member_leave" : "member_remove",
      () => send("DELETE", `/api/organizations/${encodeURIComponent(organizationId)}/members/${encodeURIComponent(member.userId)}`),
      (r) => r.status === 204,
    );
    setBusy(false);
    setConfirming(null);
    if (status === 204) {
      if (member.isSelf) {
        router.push("/organizations");
      }
      router.refresh();
      return;
    }
    setMessage(status === 409 ? LAST_MEMBER : status === 404 ? "That member is no longer in this organization." : unavailableText);
    router.refresh();
  }

  return (
    <section className="card" aria-labelledby="members-heading">
      <h2 id="members-heading">Members</h2>
      <table className="table">
        <thead>
          <tr>
            <th scope="col">Email</th>
            <th scope="col">Joined</th>
            <th scope="col">
              <span className="visually-hidden">Actions</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {members.map((m) => (
            <tr key={m.userId} data-testid="member-row">
              <td className="cell-wrap">
                {m.email}
                {m.isSelf && <span className="muted"> (you)</span>}
              </td>
              <td>{formatUtc(m.joinedAt)}</td>
              <td className="cell-actions">
                {confirming === m.userId ? (
                  <>
                    <button type="button" className="button button-danger" disabled={busy} onClick={() => remove(m)}>
                      {m.isSelf ? "Confirm leave" : `Confirm remove`}
                    </button>
                    <button type="button" className="button" disabled={busy} onClick={() => setConfirming(null)}>
                      Cancel
                    </button>
                  </>
                ) : (
                  <button type="button" className="button" disabled={busy} onClick={() => setConfirming(m.userId)} aria-label={m.isSelf ? "Leave organization" : `Remove ${m.email}`}>
                    {m.isSelf ? "Leave" : "Remove"}
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <p role="alert" className="form-error" data-testid="member-error">
        {message}
      </p>
    </section>
  );
}
