"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { send, unavailableText } from "@/lib/client";
import { traced } from "@/telemetry/ui";

export const INVALID_INVITATION = "This invitation is invalid or has expired.";

const failureText: Record<number, string> = {
  400: INVALID_INVITATION,
  429: "Too many attempts. Wait a minute and try again.",
};

/** One generic failure for used, revoked, expired and unknown codes (FR-010). */
export function RedeemInvitationForm() {
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function onSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const code = new FormData(event.currentTarget).get("code");
    setBusy(true);
    setError(null);
    const { status, json } = await traced("invite_redeem", () => send("POST", "/api/invitations/redeem", { code }), (r) => r.status === 200);
    setBusy(false);
    if (status === 200) {
      router.push(`/organizations/${encodeURIComponent((json as { organizationId: string }).organizationId)}`);
      router.refresh();
      return;
    }
    setError(failureText[status] ?? unavailableText);
  }

  return (
    <form className="card form" onSubmit={onSubmit} noValidate aria-describedby="redeem-error">
      <div className="field">
        <label htmlFor="invitation-code">Invitation code</label>
        <input id="invitation-code" name="code" required maxLength={64} autoComplete="off" spellCheck={false} className="code-input" />
        <p className="field-hint">Ask a member of the organization for a code. Codes work once and expire.</p>
      </div>
      <p id="redeem-error" role="alert" className="form-error" data-testid="redeem-error">
        {error}
      </p>
      <button type="submit" className="button button-primary" disabled={busy}>
        {busy ? "Joining…" : "Join organization"}
      </button>
    </form>
  );
}
