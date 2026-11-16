"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { send } from "@/lib/client";
import type { Organization } from "@/lib/organizations";
import { traced } from "@/telemetry/ui";

const NONE = "";

/**
 * Context switcher (FR-004, FR-005). Lists only the caller's organizations; selecting one records a
 * preference and opens it. The selection is never authority — every page and call re-checks membership.
 */
export function OrganizationSwitcher({ organizations, activeOrganizationId }: { organizations: Organization[]; activeOrganizationId: string | null }) {
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  if (organizations.length === 0) return null;

  async function onChange(event: React.ChangeEvent<HTMLSelectElement>) {
    const id = event.target.value;
    if (!id) return;
    setBusy(true);
    setError(null);
    const { status } = await traced("org_select", () => send("POST", `/api/organizations/${encodeURIComponent(id)}/select`), (r) => r.status === 204);
    setBusy(false);
    if (status === 204) {
      router.push(`/organizations/${encodeURIComponent(id)}`);
      router.refresh();
    } else {
      setError("Couldn't switch organization.");
      router.refresh();
    }
  }

  return (
    <div className="switcher">
      <label htmlFor="org-switcher">Active organization</label>
      <select id="org-switcher" data-testid="org-switcher" value={activeOrganizationId ?? NONE} onChange={onChange} disabled={busy}>
        {activeOrganizationId === null && <option value={NONE}>Choose…</option>}
        {organizations.map((o) => (
          <option key={o.id} value={o.id}>
            {o.displayName}
          </option>
        ))}
      </select>
      <p role="status" aria-live="polite" className="form-error">
        {error}
      </p>
    </div>
  );
}
