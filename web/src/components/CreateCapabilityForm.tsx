"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { forbiddenNoticePath, forbiddenText, send, unavailableText } from "@/lib/client";
import { errorCode, fieldError, NAME_CONFLICT, NAME_RULE, SUSPENDED } from "@/lib/publishers";
import { traced } from "@/telemetry/ui";

/**
 * Publisher members create a capability (FR-012). The API allocates its canonical id and reserves
 * `<handle>/<name>` for it forever (FR-013, FR-014); the name is sent exactly as typed (US4).
 */
export function CreateCapabilityForm({ organizationId, publisherId }: { organizationId: string; publisherId: string }) {
  const router = useRouter();
  const [errors, setErrors] = useState<{ name?: string | null; displayName?: string | null; form?: string | null }>({});
  const [busy, setBusy] = useState(false);

  async function onSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    setBusy(true);
    setErrors({});
    const { status, json } = await traced(
      "capability_create",
      () =>
        send("POST", `/api/organizations/${encodeURIComponent(organizationId)}/publishers/${encodeURIComponent(publisherId)}/capabilities`, {
          name: data.get("name"),
          displayName: data.get("displayName"),
        }),
      (r) => r.status === 201,
    );
    setBusy(false);
    if (status === 201) {
      form.reset();
      router.refresh();
      return;
    }

    if (status === 400) {
      setErrors({ name: fieldError(json, "name"), displayName: fieldError(json, "displayName") });
      return;
    }

    const code = errorCode(json);
    setErrors({
      form:
        status === 409 && code === "conflict"
          ? NAME_CONFLICT
          : status === 409 && code === "suspended"
            ? SUSPENDED
            : status === 404
              ? "Publisher not found."
              : status === 403
                ? forbiddenText
                : unavailableText,
    });
    if (status === 403) router.replace(forbiddenNoticePath(organizationId));
    if (status === 409 && code === "suspended") router.refresh();
  }

  return (
    <form className="card form" onSubmit={onSubmit} noValidate aria-labelledby="create-capability-heading">
      <h2 id="create-capability-heading">Create a capability</h2>
      <div className="field">
        <label htmlFor="capability-name">Name</label>
        <input
          id="capability-name"
          name="name"
          required
          maxLength={64}
          autoComplete="off"
          autoCapitalize="none"
          spellCheck={false}
          aria-invalid={errors.name ? true : undefined}
          aria-describedby="capability-name-hint capability-name-error"
        />
        <p id="capability-name-hint" className="field-hint">
          {NAME_RULE} The name and its alias are permanent.
        </p>
        <p id="capability-name-error" role="alert" className="form-error">
          {errors.name}
        </p>
      </div>
      <div className="field">
        <label htmlFor="capability-display-name">Display name</label>
        <input
          id="capability-display-name"
          name="displayName"
          required
          maxLength={100}
          autoComplete="off"
          aria-invalid={errors.displayName ? true : undefined}
          aria-describedby="capability-display-name-error"
        />
        <p id="capability-display-name-error" role="alert" className="form-error">
          {errors.displayName}
        </p>
      </div>
      <div className="form-actions">
        <button type="submit" className="button button-primary" disabled={busy}>
          {busy ? "Creating…" : "Create capability"}
        </button>
      </div>
      <p role="alert" className="form-error" data-testid="capability-error">
        {errors.form}
      </p>
    </form>
  );
}
