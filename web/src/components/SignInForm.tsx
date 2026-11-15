"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { traced } from "@/telemetry/ui";

const failureText: Record<number, string> = {
  401: "Email or password is incorrect.",
  429: "Too many attempts. Wait a minute and try again.",
};

/** Generic failures only (FR-020): the message never says whether the account exists or is locked. */
export function SignInForm({ returnTo, registrationEnabled }: { returnTo: string; registrationEnabled: boolean }) {
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function onSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    setBusy(true);
    setError(null);
    const status = await traced("sign_in", async () => {
      try {
        const res = await fetch("/api/auth/sign-in", {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({ email: form.get("email"), password: form.get("password") }),
        });
        return res.status;
      } catch {
        return 503;
      }
    });
    if (status === 200) {
      router.replace(returnTo);
      router.refresh();
      return;
    }
    setBusy(false);
    setError(failureText[status] ?? "The service is unavailable. Try again shortly.");
  }

  return (
    <form className="card form" onSubmit={onSubmit} noValidate aria-describedby="sign-in-error">
      <div className="field">
        <label htmlFor="email">Email</label>
        <input id="email" name="email" type="email" autoComplete="username" required maxLength={254} />
      </div>
      <div className="field">
        <label htmlFor="password">Password</label>
        <input id="password" name="password" type="password" autoComplete="current-password" required maxLength={128} />
      </div>
      <p id="sign-in-error" role="alert" className="form-error" data-testid="form-error">
        {error}
      </p>
      <button type="submit" className="button button-primary" disabled={busy}>
        {busy ? "Signing in…" : "Sign in"}
      </button>
      {registrationEnabled && (
        <p className="form-alt">
          New to Gözleme? <a href="/register">Create an account</a>
        </p>
      )}
    </form>
  );
}
