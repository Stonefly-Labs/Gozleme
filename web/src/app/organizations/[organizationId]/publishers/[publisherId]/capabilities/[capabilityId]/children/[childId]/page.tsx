import type { Metadata } from "next";
import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { AccountBar } from "@/components/AccountBar";
import { ChildDetail } from "@/components/ChildDetail";
import { OrganizationsUnavailable } from "@/components/OrganizationsUnavailable";
import { SignedInHeader } from "@/components/SignedInHeader";
import { childPage, childrenPage } from "@/lib/children";
import { getChild } from "@/lib/children-server";
import { cursor } from "@/lib/revisions-server";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";
// A fixed title: native names and titles stay out of document titles and browser telemetry.
export const metadata: Metadata = { title: "Child — Gözleme" };

/**
 * One child and its history over the capability's revisions (GOZ-0008 User Stories 1 and 2). Unknown, malformed,
 * foreign or misplaced ids (a revision or generation ID among them) all get the same "not found".
 */
export default async function ChildPage({
  params,
  searchParams,
}: {
  params: Promise<{ organizationId: string; publisherId: string; capabilityId: string; childId: string }>;
  searchParams: Promise<{ after?: string | string[] }>;
}) {
  const { organizationId, publisherId, capabilityId, childId } = await params;
  const after = cursor((await searchParams).after);
  const listHref = childrenPage(organizationId, publisherId, capabilityId);
  const returnTo = `/login?returnTo=${encodeURIComponent(childPage(organizationId, publisherId, capabilityId, childId))}`;
  const session = await getSession();
  if (session.state === "anonymous") redirect(returnTo);

  const detail = session.state === "authenticated" ? await getChild(organizationId, publisherId, capabilityId, childId, after) : ({ state: "unavailable" } as const);
  if (detail.state === "anonymous") redirect(returnTo);
  if (detail.state === "not_found") notFound();

  return (
    <>
      {session.state === "authenticated" && <SignedInHeader />}
      <main className="shell">
        <header className="header">
          <div>
            <p className="eyebrow">
              <Link href={`/organizations/${encodeURIComponent(organizationId)}`}>Organization</Link> ·{" "}
              <Link href={`/organizations/${encodeURIComponent(organizationId)}/publishers/${encodeURIComponent(publisherId)}`}>Publisher</Link> ·{" "}
              <Link href={listHref} data-testid="back-to-children">
                Children
              </Link>
            </p>
            <h1>Child</h1>
          </div>
          {session.state === "authenticated" && <AccountBar email={session.session.email} />}
        </header>
        {detail.state !== "ok" ? (
          <OrganizationsUnavailable />
        ) : (
          <ChildDetail organizationId={organizationId} publisherId={publisherId} capabilityId={capabilityId} detail={detail.data} />
        )}
      </main>
    </>
  );
}
