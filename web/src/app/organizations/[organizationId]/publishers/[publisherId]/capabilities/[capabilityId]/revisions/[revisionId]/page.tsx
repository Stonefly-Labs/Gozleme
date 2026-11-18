import type { Metadata } from "next";
import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { AccountBar } from "@/components/AccountBar";
import { OrganizationsUnavailable } from "@/components/OrganizationsUnavailable";
import { RevisionDetail } from "@/components/RevisionDetail";
import { SignedInHeader } from "@/components/SignedInHeader";
import { revisionsPage } from "@/lib/revisions";
import { getRevision } from "@/lib/revisions-server";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";
// A fixed title: versions and names stay out of document titles and browser telemetry.
export const metadata: Metadata = { title: "Revision — Gözleme" };

/**
 * One signed revision (GOZ-0007 User Stories 1 and 3): Native, Projection and Attestation tabs. The API verifies it
 * from the stored bytes on every load; unknown, malformed, foreign or misplaced ids all get the same "not found".
 */
export default async function RevisionPage({
  params,
}: {
  params: Promise<{ organizationId: string; publisherId: string; capabilityId: string; revisionId: string }>;
}) {
  const { organizationId, publisherId, capabilityId, revisionId } = await params;
  const listHref = revisionsPage(organizationId, publisherId, capabilityId);
  const returnTo = `/login?returnTo=${encodeURIComponent(`${listHref}/${encodeURIComponent(revisionId)}`)}`;
  const session = await getSession();
  if (session.state === "anonymous") redirect(returnTo);

  const revision = session.state === "authenticated" ? await getRevision(organizationId, publisherId, capabilityId, revisionId) : ({ state: "unavailable" } as const);
  if (revision.state === "anonymous") redirect(returnTo);
  if (revision.state === "not_found") notFound();

  return (
    <>
      {session.state === "authenticated" && <SignedInHeader />}
      <main className="shell">
        <header className="header">
          <div>
            <p className="eyebrow">
              <Link href={`/organizations/${encodeURIComponent(organizationId)}`}>Organization</Link> ·{" "}
              <Link href={`/organizations/${encodeURIComponent(organizationId)}/publishers/${encodeURIComponent(publisherId)}`}>Publisher</Link> ·{" "}
              <Link href={listHref} data-testid="back-to-revisions">
                Revisions
              </Link>
            </p>
            <h1>Revision</h1>
          </div>
          {session.state === "authenticated" && <AccountBar email={session.session.email} />}
        </header>
        {revision.state !== "ok" ? (
          <OrganizationsUnavailable />
        ) : (
          <RevisionDetail organizationId={organizationId} publisherId={publisherId} capabilityId={capabilityId} revision={revision.data} />
        )}
      </main>
    </>
  );
}
