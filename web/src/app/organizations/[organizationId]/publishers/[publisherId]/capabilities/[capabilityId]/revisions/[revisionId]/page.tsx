import type { Metadata } from "next";
import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { AccountBar } from "@/components/AccountBar";
import { OrganizationsUnavailable } from "@/components/OrganizationsUnavailable";
import { ProjectionGenerations } from "@/components/ProjectionGenerations";
import { RevisionChildren } from "@/components/RevisionChildren";
import { RevisionDetail } from "@/components/RevisionDetail";
import { SignedInHeader } from "@/components/SignedInHeader";
import { listGenerations, listRevisionChildren } from "@/lib/children-server";
import { revisionsPage } from "@/lib/revisions";
import { getRevision } from "@/lib/revisions-server";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";
// A fixed title: versions and names stay out of document titles and browser telemetry.
export const metadata: Metadata = { title: "Revision — Gözleme" };

/**
 * One signed revision (GOZ-0007 User Stories 1 and 3): Native, Projection and Attestation tabs. The API verifies it
 * from the stored bytes on every load; unknown, malformed, foreign or misplaced ids all get the same "not found".
 * GOZ-0008 adds its **Children** and **Projection generations** sections.
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

  const [revision, children, generations] =
    session.state === "authenticated"
      ? await Promise.all([
          getRevision(organizationId, publisherId, capabilityId, revisionId),
          listRevisionChildren(organizationId, publisherId, capabilityId, revisionId),
          listGenerations(organizationId, publisherId, capabilityId, revisionId),
        ])
      : [{ state: "unavailable" } as const, { state: "unavailable" } as const, { state: "unavailable" } as const];
  if (revision.state === "anonymous" || children.state === "anonymous" || generations.state === "anonymous") redirect(returnTo);
  if (revision.state === "not_found" || children.state === "not_found" || generations.state === "not_found") notFound();

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
        {revision.state !== "ok" || children.state !== "ok" || generations.state !== "ok" ? (
          <OrganizationsUnavailable />
        ) : (
          <div className="stack">
            <RevisionDetail organizationId={organizationId} publisherId={publisherId} capabilityId={capabilityId} revision={revision.data} />
            <RevisionChildren organizationId={organizationId} publisherId={publisherId} capabilityId={capabilityId} list={children.data} />
            <ProjectionGenerations organizationId={organizationId} publisherId={publisherId} capabilityId={capabilityId} revisionId={revision.data.id} list={generations.data} />
          </div>
        )}
      </main>
    </>
  );
}
