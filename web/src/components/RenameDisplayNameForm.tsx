"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { displayNameError, forbiddenNoticePath, forbiddenText, send, unavailableText } from "@/lib/client";
import { errorCode, SUSPENDED } from "@/lib/publishers";
import { traced } from "@/telemetry/ui";

/** Changes only a display name (FR-006, FR-015): ids, handles, names and aliases never change. */
export function RenameDisplayNameForm({
  organizationId,
  path,
  flow,
  label,
  displayName,
  inputId,
}: {
  organizationId: string;
  /** Same-origin route handler path. */
  path: string;
  flow: "publisher_rename" | "capability_rename";
  label: string;
  displayName: string;
  inputId: string;
}) {
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
    const { status, json } = await traced(flow, () => send("PATCH", path, { displayName: value }), (r) => r.status === 200);
    setBusy(false);
    if (status === 200) {
      setSaved(true);
      router.refresh();
      return;
    }

    setError(
      status === 400
        ? (displayNameError(json) ?? "Enter a name of 1–100 characters.")
        : status === 409 && errorCode(json) === "suspended"
          ? SUSPENDED
          : status === 404
            ? "Not found."
            : status === 403
              ? forbiddenText
              : unavailableText,
    );
    if (status === 403) router.replace(forbiddenNoticePath(organizationId));
    if (status === 409) router.refresh();
  }

  return (
    <form className="form form-inline" onSubmit={onSubmit} noValidate aria-label={`Rename ${label}`}>
      <div className="field">
        <label htmlFor={inputId}>{label}</label>
        <input
          id={inputId}
          name="displayName"
          defaultValue={displayName}
          required
          maxLength={100}
          autoComplete="off"
          aria-invalid={error ? true : undefined}
          aria-describedby={`${inputId}-error`}
        />
        <p id={`${inputId}-error`} role="alert" className="form-error">
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
