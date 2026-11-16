"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { formatUtc } from "@/lib/format";
import { forbiddenText, send, unavailableText } from "@/lib/client";
import type { Member, Role } from "@/lib/organizations";
import { traced } from "@/telemetry/ui";

export const LAST_MEMBER = "An organization must keep at least one member.";
export const LAST_ADMIN = "An organization needs at least one admin.";

/** Roles an admin may grant; `member` is always held and shown fixed (GOZ-0004 contracts/permissions.md). */
const GRANTABLE: Exclude<Role, "member">[] = ["admin", "publisher", "reviewer", "approver"];

/**
 * Members (GOZ-0002 FR-014–FR-016, GOZ-0004 FR-007–FR-012). Everyone sees each member's roles and may
 * leave; removing others and editing roles appear only when the caller's permissions include them. The
 * API decides every request again, so a stale page gets a 403 and refreshes.
 */
export function MemberList({ organizationId, members, permissions }: { organizationId: string; members: Member[]; permissions: string[] }) {
  const router = useRouter();
  const [confirming, setConfirming] = useState<string | null>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const canRemove = permissions.includes("membership.remove");
  const canEditRoles = permissions.includes("membership.roles.change");
  const base = `/api/organizations/${encodeURIComponent(organizationId)}/members`;

  async function remove(member: Member) {
    setBusy(true);
    setMessage(null);
    const { status, json } = await traced(
      member.isSelf ? "member_leave" : "member_remove",
      () => send("DELETE", `${base}/${encodeURIComponent(member.userId)}`),
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
    setMessage(refusal(status, json));
    router.refresh();
  }

  async function saveRoles(member: Member, event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const roles = GRANTABLE.filter((r) => form.get(r) === "on");
    setBusy(true);
    setMessage(null);
    const { status, json } = await traced(
      "member_roles",
      () => send("PUT", `${base}/${encodeURIComponent(member.userId)}/roles`, { roles: ["member", ...roles] }),
      (r) => r.status === 200,
    );
    setBusy(false);
    if (status === 200) {
      setEditing(null);
      router.refresh();
      return;
    }
    setMessage(refusal(status, json));
    router.refresh();
  }

  return (
    <section className="card" aria-labelledby="members-heading">
      <h2 id="members-heading">Members</h2>
      <table className="table">
        <thead>
          <tr>
            <th scope="col">Email</th>
            <th scope="col">Roles</th>
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
              <td>
                <ul className="role-list" aria-label={`Roles of ${m.email}`}>
                  {m.roles.map((r) => (
                    <li key={r} className="badge badge-unknown" data-testid="role-badge">
                      {r}
                    </li>
                  ))}
                </ul>
                {editing === m.userId && (
                  <form className="role-editor" onSubmit={(e) => saveRoles(m, e)} aria-label={`Edit roles of ${m.email}`}>
                    <label className="role-option">
                      <input type="checkbox" checked disabled readOnly /> member <span className="muted">(always)</span>
                    </label>
                    {GRANTABLE.map((r) => (
                      <label key={r} className="role-option">
                        <input type="checkbox" name={r} defaultChecked={m.roles.includes(r)} /> {r}
                      </label>
                    ))}
                    <div className="form-actions">
                      <button type="submit" className="button" disabled={busy}>
                        {busy ? "Saving…" : "Save roles"}
                      </button>
                      <button type="button" className="button" disabled={busy} onClick={() => setEditing(null)}>
                        Cancel
                      </button>
                    </div>
                  </form>
                )}
              </td>
              <td>{formatUtc(m.joinedAt)}</td>
              <td className="cell-actions">
                {canEditRoles && editing !== m.userId && (
                  <button type="button" className="button" disabled={busy} onClick={() => setEditing(m.userId)} aria-label={`Edit roles of ${m.email}`}>
                    Edit roles
                  </button>
                )}
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
                  (m.isSelf || canRemove) && (
                    <button type="button" className="button" disabled={busy} onClick={() => setConfirming(m.userId)} aria-label={m.isSelf ? "Leave organization" : `Remove ${m.email}`}>
                      {m.isSelf ? "Leave" : "Remove"}
                    </button>
                  )
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

function refusal(status: number, json: unknown): string {
  const error = (json as { error?: string } | null)?.error;
  if (status === 403) return forbiddenText;
  if (status === 409) return error === "last_admin" ? LAST_ADMIN : LAST_MEMBER;
  if (status === 404) return "That member is no longer in this organization.";
  if (status === 400) return "Choose only the listed roles.";
  return unavailableText;
}
