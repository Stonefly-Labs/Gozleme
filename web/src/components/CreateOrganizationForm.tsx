"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { displayNameError, send, unavailableText } from "@/lib/client";
import { traced } from "@/telemetry/ui";

/** Any signed-in account may create an organization and becomes its first member (FR-001). */
export function CreateOrganizationForm() {
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function onSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const displayName = new FormData(form).get("displayName");
    setBusy(true);
    setError(null);
    const { status, json } = await traced("org_create", () => send("POST", "/api/organizations", { displayName }), (r) => r.status === 201);
    setBusy(false);
    if (status === 201) {
      form.reset();
      router.push(`/organizations/${encodeURIComponent((json as { id: string }).id)}`);
      router.refresh();
      return;
    }
    setError(status === 400 ? (displayNameError(json) ?? "Enter a name of 1–100 characters.") : unavailableText);
  }

  return (
    <form className="card form" onSubmit={onSubmit} noValidate aria-labelledby="create-org-heading">
      <h2 id="create-org-heading">Create an organization</h2>
      <div className="field">
        <label htmlFor="org-name">Organization name</label>
        <input
          id="org-name"
          name="displayName"
          required
          maxLength={100}
          aria-invalid={error ? true : undefined}
          aria-describedby="org-name-error"
          autoComplete="off"
        />
        <p id="org-name-error" role="alert" className="form-error" data-testid="create-org-error">
          {error}
        </p>
      </div>
      <button type="submit" className="button button-primary" disabled={busy}>
        {busy ? "Creating…" : "Create organization"}
      </button>
    </form>
  );
}
