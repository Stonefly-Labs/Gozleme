import type { Metadata } from "next";
import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { AccountBar } from "@/components/AccountBar";
import { CanonicalId } from "@/components/CanonicalId";
import { OrganizationsUnavailable } from "@/components/OrganizationsUnavailable";
import { RevisionList } from "@/components/RevisionList";
import { SignedInHeader } from "@/components/SignedInHeader";
import { getOrganization } from "@/lib/organizations";
import { getCapability } from "@/lib/publishers-server";
import { signingKeysPage } from "@/lib/revisions";
import { cursor, listRevisions } from "@/lib/revisions-server";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";
// A fixed title: capability names and versions stay out of document titles and browser telemetry.
export const metadata: Metadata = { title: "Revisions — Gözleme" };

/**
 * A capability's signed revisions in publication order (GOZ-0007 User Story 1). The API re-checks membership and
 * verifies every revision on each load, so non-members and unknown, malformed or foreign ids all get the same
 * "not found" page.
 */
export default async function RevisionsPage({
  params,
  searchParams,
}: {
  params: Promise<{ organizationId: string; publisherId: string; capabilityId: string }>;
  searchParams: Promise<{ after?: string | string[] }>;
}) {
  const { organizationId, publisherId, capabilityId } = await params;
  const after = cursor((await searchParams).after);
  const publisherHref = `/organizations/${encodeURIComponent(organizationId)}/publishers/${encodeURIComponent(publisherId)}`;
  const returnTo = `/login?returnTo=${encodeURIComponent(`${publisherHref}/capabilities/${encodeURIComponent(capabilityId)}/revisions`)}`;
  const session = await getSession();
  if (session.state === "anonymous") redirect(returnTo);

  const [organization, capability, list] =
    session.state === "authenticated"
      ? await Promise.all([getOrganization(organizationId), getCapability(organizationId, publisherId, capabilityId), listRevisions(organizationId, publisherId, capabilityId, after)])
      : [{ state: "unavailable" } as const, { state: "unavailable" } as const, { state: "unavailable" } as const];
  if (organization.state === "anonymous" || capability.state === "anonymous" || list.state === "anonymous") redirect(returnTo);
  if (organization.state === "not_found" || capability.state === "not_found" || list.state === "not_found") notFound();

  const ready = organization.state === "ok" && capability.state === "ok" && list.state === "ok";
  return (
    <>
      {session.state === "authenticated" && <SignedInHeader />}
      <main className="shell">
        <header className="header">
          <div>
            <p className="eyebrow">
              <Link href={`/organizations/${encodeURIComponent(organizationId)}`}>Organization</Link> · <Link href={publisherHref}>Publisher</Link> ·
              Revisions
            </p>
            <h1 data-testid="capability-name">{capability.state === "ok" ? capability.data.displayName : "Capability"}</h1>
          </div>
          {session.state === "authenticated" && <AccountBar email={session.session.email} />}
        </header>
        {!ready ? (
          <OrganizationsUnavailable />
        ) : (
          <div className="stack">
            <section className="card" aria-labelledby="capability-heading">
              <h2 id="capability-heading">
                <code data-testid="capability-alias">{capability.data.alias}</code>
              </h2>
              <dl>
                <div className="row">
                  <dt>Canonical ID</dt>
                  <dd>
                    <CanonicalId value={capability.data.canonicalId} label="canonical ID" testId="capability-canonical-id" />
                  </dd>
                </div>
              </dl>
              <p className="form-inline">
                <Link href={signingKeysPage(organizationId, publisherId)} data-testid="signing-keys-link">
                  Signing keys
                </Link>
              </p>
            </section>
            <RevisionList organizationId={organizationId} publisherId={publisherId} capabilityId={capability.data.id} list={list.data} />
          </div>
        )}
      </main>
    </>
  );
}
