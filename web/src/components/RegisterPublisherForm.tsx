"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { forbiddenNoticePath, forbiddenText, send, unavailableText } from "@/lib/client";
import { errorCode, fieldError, HANDLE_RULE, HANDLE_UNAVAILABLE, type Publisher, RATE_LIMITED } from "@/lib/publishers";
import { traced } from "@/telemetry/ui";
import { CanonicalId } from "./CanonicalId";

/**
 * Admins register a publisher handle (FR-005). The handle field starts empty with no suggestion, placeholder or
 * autocomplete taken from the organization name or an email (FR-010), and the typed value is sent exactly as is:
 * the API decides and never normalizes (US4). The confirmation names the organization by its opaque id (FR-009).
 */
export function RegisterPublisherForm({ organizationId }: { organizationId: string }) {
  const router = useRouter();
  const [errors, setErrors] = useState<{ handle?: string | null; displayName?: string | null; form?: string | null }>({});
  const [registered, setRegistered] = useState<Publisher | null>(null);
  const [busy, setBusy] = useState(false);

  async function onSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    setBusy(true);
    setErrors({});
    setRegistered(null);
    const { status, json } = await traced(
      "publisher_register",
      () => send("POST", `/api/organizations/${encodeURIComponent(organizationId)}/publishers`, { handle: data.get("handle"), displayName: data.get("displayName") }),
      (r) => r.status === 201,
    );
    setBusy(false);
    if (status === 201) {
      setRegistered(json as Publisher);
      form.reset();
      router.refresh();
      return;
    }

    if (status === 400) {
      setErrors({ handle: fieldError(json, "handle"), displayName: fieldError(json, "displayName") });
      return;
    }

    setErrors({
      form:
        status === 409 && errorCode(json) === "handle_unavailable"
          ? HANDLE_UNAVAILABLE
          : status === 429
            ? RATE_LIMITED
            : status === 404
              ? "Organization not found."
              : status === 403
                ? forbiddenText
                : unavailableText,
    });
    if (status === 403) router.replace(forbiddenNoticePath(organizationId));
  }

  return (
    <form className="card form" onSubmit={onSubmit} noValidate aria-labelledby="register-publisher-heading">
      <h2 id="register-publisher-heading">Register a publisher</h2>
      <div className="field">
        <label htmlFor="publisher-handle">Handle</label>
        <input
          id="publisher-handle"
          name="handle"
          defaultValue=""
          required
          maxLength={39}
          autoComplete="off"
          autoCapitalize="none"
          spellCheck={false}
          aria-invalid={errors.handle ? true : undefined}
          aria-describedby="publisher-handle-hint publisher-handle-error"
        />
        <p id="publisher-handle-hint" className="field-hint">
          {HANDLE_RULE} A handle is permanent and unique across this registry.
        </p>
        <p id="publisher-handle-error" role="alert" className="form-error">
          {errors.handle}
        </p>
      </div>
      <div className="field">
        <label htmlFor="publisher-display-name">Display name</label>
        <input
          id="publisher-display-name"
          name="displayName"
          required
          maxLength={100}
          autoComplete="off"
          aria-invalid={errors.displayName ? true : undefined}
          aria-describedby="publisher-display-name-error"
        />
        <p id="publisher-display-name-error" role="alert" className="form-error">
          {errors.displayName}
        </p>
      </div>
      <div className="form-actions">
        <button type="submit" className="button button-primary" disabled={busy}>
          {busy ? "Registering…" : "Register publisher"}
        </button>
      </div>
      <p role="alert" className="form-error" data-testid="register-error">
        {errors.form}
      </p>
      {registered && (
        <div className="code-reveal" role="status" data-testid="register-confirmation">
          <p>
            Registered <code data-testid="confirmed-handle">{registered.handle}</code> for organization{" "}
            <code data-testid="confirmed-organization">{registered.organizationId}</code>.
          </p>
          <p>
            Publisher ID <CanonicalId value={registered.publisherId} label="publisher ID" testId="confirmed-publisher-id" />
          </p>
        </div>
      )}
    </form>
  );
}
