"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { traced } from "@/telemetry/ui";

/** Signed-in account and session controls (FR-023). Sign-out ends the session server-side, then leaves. */
export function AccountBar({ email }: { email: string }) {
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function end(path: "sign-out" | "sign-out-all") {
    setBusy(true);
    setError(null);
    const ok = await traced(path === "sign-out" ? "sign_out" : "sign_out_all", async () => {
      const res = await fetch(`/api/auth/${path}`, { method: "POST" });
      return res.ok;
    });
    if (ok) {
      router.replace("/login");
      router.refresh();
    } else {
      setBusy(false);
      setError("Couldn't sign out. Try again.");
    }
  }

  return (
    <div className="account" aria-label="Account">
      <p className="account-email">
        Signed in as <strong data-testid="account-email">{email}</strong>
      </p>
      <div className="account-actions">
        <button type="button" className="button" disabled={busy} onClick={() => end("sign-out")}>
          Sign out
        </button>
        <button type="button" className="button" disabled={busy} onClick={() => end("sign-out-all")}>
          Sign out everywhere
        </button>
      </div>
      <p role="status" aria-live="polite" className="form-error">
        {error}
      </p>
    </div>
  );
}
