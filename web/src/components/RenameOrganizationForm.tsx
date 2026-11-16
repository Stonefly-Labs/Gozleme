"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { displayNameError, forbiddenText, send, unavailableText } from "@/lib/client";
import { traced } from "@/telemetry/ui";

/** Admins may rename (GOZ-0004); the organization's id never changes (FR-003). */
export function RenameOrganizationForm({ organizationId, displayName }: { organizationId: string; displayName: string }) {
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);

  async function onSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const value = new FormData(event.currentTarget).get("displayName");
    setBusy(true);
    setError(null);
    setSaved(false);
    const { status, json } = await traced(
      "org_rename",
      () => send("PATCH", `/api/organizations/${encodeURIComponent(organizationId)}`, { displayName: value }),
      (r) => r.status === 200,
    );
    setBusy(false);
    if (status === 200) {
      setSaved(true);
      router.refresh();
      return;
    }
    setError(
      status === 400 ? (displayNameError(json) ?? "Enter a name of 1–100 characters.") : status === 404 ? "Organization not found." : status === 403 ? forbiddenText : unavailableText,
    );
    // No refresh on 403: the refreshed page drops this form, and the message with it.
  }

  return (
    <form className="card form" onSubmit={onSubmit} noValidate aria-labelledby="rename-heading">
      <h2 id="rename-heading">Name</h2>
      <div className="field">
        <label htmlFor="rename-org">Organization name</label>
        <input
          id="rename-org"
          name="displayName"
          defaultValue={displayName}
          required
          maxLength={100}
          aria-invalid={error ? true : undefined}
          aria-describedby="rename-error"
          autoComplete="off"
        />
        <p id="rename-error" role="alert" className="form-error">
          {error}
        </p>
      </div>
      <div className="form-actions">
        <button type="submit" className="button" disabled={busy}>
          {busy ? "Saving…" : "Rename"}
        </button>
        <p role="status" aria-live="polite" className="form-alt">
          {saved ? "Saved." : null}
        </p>
      </div>
    </form>
  );
}
