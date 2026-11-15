"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { traced } from "@/telemetry/ui";

export const MIN_PASSWORD = 12;
export const MAX_PASSWORD = 128;

type Fields = Partial<Record<"email" | "password" | "confirm", string[]>>;

/** Registration (FR-001–FR-007): field-level, accessible errors; duplicates get a generic message. */
export function RegisterForm() {
  const router = useRouter();
  const [fields, setFields] = useState<Fields>({});
  const [error, setError] = useState<string | null>(null);
  const [closed, setClosed] = useState(false);
  const [busy, setBusy] = useState(false);

  async function onSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const password = String(form.get("password") ?? "");
    const local: Fields = {};
    if (password.length < MIN_PASSWORD) local.password = [`Use at least ${MIN_PASSWORD} characters.`];
    if (password !== form.get("confirm")) local.confirm = ["The passwords don't match."];
    setError(null);
    setFields(local);
    if (Object.keys(local).length > 0) return;

    setBusy(true);
    const res = await traced("register", async () => {
      try {
        const r = await fetch("/api/auth/register", {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({ email: form.get("email"), password }),
        });
        return { status: r.status, body: (await r.json().catch(() => null)) as { error?: string; fields?: Fields } | null };
      } catch {
        return { status: 503, body: null };
      }
    });
    if (res.status === 201) {
      router.replace("/");
      router.refresh();
      return;
    }
    setBusy(false);
    if (res.status === 400 && res.body?.fields) setFields(res.body.fields);
    else if (res.body?.error === "registration_closed") setClosed(true);
    else if (res.status === 409) setError("We couldn't create an account with these details. If you already have one, sign in.");
    else if (res.status === 429) setError("Too many attempts. Wait a minute and try again.");
    else setError("The service is unavailable. Try again shortly.");
  }

  if (closed) return <RegistrationClosed />;

  const describe = (name: keyof Fields) => (fields[name] ? `${name}-error` : undefined);
  const fieldError = (name: keyof Fields) =>
    fields[name] && (
      <p id={`${name}-error`} className="field-error" data-testid={`${name}-error`}>
        {fields[name]!.join(" ")}
      </p>
    );

  return (
    <form className="card form" onSubmit={onSubmit} noValidate>
      <div className="field">
        <label htmlFor="email">Email</label>
        <input id="email" name="email" type="email" autoComplete="email" required maxLength={254}
          aria-invalid={!!fields.email} aria-describedby={describe("email")} />
        {fieldError("email")}
      </div>
      <div className="field">
        <label htmlFor="password">Password</label>
        <input id="password" name="password" type="password" autoComplete="new-password" required maxLength={MAX_PASSWORD}
          aria-invalid={!!fields.password} aria-describedby={["password-hint", describe("password")].filter(Boolean).join(" ")} />
        <p id="password-hint" className="field-hint">
          At least {MIN_PASSWORD} characters. A passphrase of several words works well.
        </p>
        {fieldError("password")}
      </div>
      <div className="field">
        <label htmlFor="confirm">Confirm password</label>
        <input id="confirm" name="confirm" type="password" autoComplete="new-password" required maxLength={MAX_PASSWORD}
          aria-invalid={!!fields.confirm} aria-describedby={describe("confirm")} />
        {fieldError("confirm")}
      </div>
      <p role="alert" className="form-error" data-testid="form-error">
        {error}
      </p>
      <button type="submit" className="button button-primary" disabled={busy}>
        {busy ? "Creating account…" : "Create account"}
      </button>
      <p className="form-alt">
        Already have an account? <a href="/login">Sign in</a>
      </p>
      <p className="field-hint">Your email address is not verified yet; it is only used to sign in.</p>
    </form>
  );
}

export function RegistrationClosed() {
  return (
    <div className="card" role="status" data-testid="registration-closed">
      <h2>Registration is closed</h2>
      <p>This Gözleme deployment doesn&apos;t accept new accounts. Ask an operator for access.</p>
      <p>
        <a href="/login">Sign in</a> with an existing account.
      </p>
    </div>
  );
}
