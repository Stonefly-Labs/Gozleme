import Link from "next/link";
import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { AccountBar } from "@/components/AccountBar";
import { CreateOrganizationForm } from "@/components/CreateOrganizationForm";
import { OrganizationsUnavailable } from "@/components/OrganizationsUnavailable";
import { SignedInHeader } from "@/components/SignedInHeader";
import { listOrganizations } from "@/lib/organizations";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";
export const metadata: Metadata = { title: "Organizations — Gözleme" };

/** The caller's organizations (FR-004) with create and join; signed-in only (IF-02). */
export default async function OrganizationsPage() {
  const session = await getSession();
  if (session.state === "anonymous") redirect("/login?returnTo=%2Forganizations");
  const list = session.state === "authenticated" ? await listOrganizations() : ({ state: "unavailable" } as const);
  if (list.state === "anonymous") redirect("/login?returnTo=%2Forganizations");

  return (
    <>
      {session.state === "authenticated" && <SignedInHeader />}
      <main className="shell">
        <header className="header">
          <div>
            <p className="eyebrow">Gözleme · AI capability registry</p>
            <h1>Organizations</h1>
          </div>
          {session.state === "authenticated" && <AccountBar email={session.session.email} />}
        </header>
        {list.state !== "ok" ? (
          <OrganizationsUnavailable />
        ) : (
          <div className="grid">
            <section className="card" aria-labelledby="yours-heading">
              <h2 id="yours-heading">Your organizations</h2>
              {list.data.organizations.length === 0 ? (
                <div data-testid="organizations-empty">
                  <p>You don&apos;t belong to any organization yet.</p>
                  <p className="form-alt">
                    Create one, or <Link href="/join">join with a code</Link> from a member.
                  </p>
                </div>
              ) : (
                <ul className="org-list" data-testid="organization-list">
                  {list.data.organizations.map((o) => (
                    <li key={o.id}>
                      <Link href={`/organizations/${encodeURIComponent(o.id)}`}>{o.displayName}</Link>
                      {o.id === list.data.activeOrganizationId && (
                        <span className="badge badge-ok" data-testid="active-badge">
                          Active
                        </span>
                      )}
                    </li>
                  ))}
                </ul>
              )}
            </section>
            <CreateOrganizationForm />
          </div>
        )}
      </main>
    </>
  );
}
