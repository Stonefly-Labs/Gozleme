import type { Metadata } from "next";
import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { AccountBar } from "@/components/AccountBar";
import { CanonicalId } from "@/components/CanonicalId";
import { ChildList } from "@/components/ChildList";
import { OrganizationsUnavailable } from "@/components/OrganizationsUnavailable";
import { SignedInHeader } from "@/components/SignedInHeader";
import { childrenPage } from "@/lib/children";
import { listChildren } from "@/lib/children-server";
import { getCapability } from "@/lib/publishers-server";
import { revisionsPage } from "@/lib/revisions";
import { cursor } from "@/lib/revisions-server";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";
// A fixed title: capability and child names stay out of document titles and browser telemetry.
export const metadata: Metadata = { title: "Children — Gözleme" };

/**
 * A capability's children (GOZ-0008 User Story 1). The API re-checks membership on each load, so non-members and
 * unknown, malformed or foreign ids all get the same "not found" page.
 */
export default async function ChildrenPage({
  params,
  searchParams,
}: {
  params: Promise<{ organizationId: string; publisherId: string; capabilityId: string }>;
  searchParams: Promise<{ after?: string | string[] }>;
}) {
  const { organizationId, publisherId, capabilityId } = await params;
  const after = cursor((await searchParams).after);
  const publisherHref = `/organizations/${encodeURIComponent(organizationId)}/publishers/${encodeURIComponent(publisherId)}`;
  const returnTo = `/login?returnTo=${encodeURIComponent(childrenPage(organizationId, publisherId, capabilityId))}`;
  const session = await getSession();
  if (session.state === "anonymous") redirect(returnTo);

  const [capability, list] =
    session.state === "authenticated"
      ? await Promise.all([getCapability(organizationId, publisherId, capabilityId), listChildren(organizationId, publisherId, capabilityId, after)])
      : [{ state: "unavailable" } as const, { state: "unavailable" } as const];
  if (capability.state === "anonymous" || list.state === "anonymous") redirect(returnTo);
  if (capability.state === "not_found" || list.state === "not_found") notFound();

  const ready = capability.state === "ok" && list.state === "ok";
  return (
    <>
      {session.state === "authenticated" && <SignedInHeader />}
      <main className="shell">
        <header className="header">
          <div>
            <p className="eyebrow">
              <Link href={`/organizations/${encodeURIComponent(organizationId)}`}>Organization</Link> · <Link href={publisherHref}>Publisher</Link> ·{" "}
              <Link href={revisionsPage(organizationId, publisherId, capabilityId)} data-testid="children-revisions-link">
                Revisions
              </Link>{" "}
              · Children
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
            </section>
            <ChildList organizationId={organizationId} publisherId={publisherId} capabilityId={capability.data.id} list={list.data} />
          </div>
        )}
      </main>
    </>
  );
}
