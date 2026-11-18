import type { Metadata } from "next";
import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { AccountBar } from "@/components/AccountBar";
import { CanonicalId } from "@/components/CanonicalId";
import { OrganizationsUnavailable } from "@/components/OrganizationsUnavailable";
import { SignedInHeader } from "@/components/SignedInHeader";
import { SigningKeyList } from "@/components/SigningKeyList";
import { StateBadge } from "@/components/StateBadge";
import { getPublisher } from "@/lib/publishers-server";
import { listSigningKeys } from "@/lib/revisions-server";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";
// A fixed title: handles and display names stay out of document titles and browser telemetry.
export const metadata: Metadata = { title: "Signing keys — Gözleme" };

/**
 * A publisher's registry-held signing keys (GOZ-0007 contracts/signing-keys.md "Key view"), readable by every member.
 * Non-members and unknown, malformed or foreign ids get the publisher "not found" page.
 */
export default async function SigningKeysPage({ params }: { params: Promise<{ organizationId: string; publisherId: string }> }) {
  const { organizationId, publisherId } = await params;
  const publisherHref = `/organizations/${encodeURIComponent(organizationId)}/publishers/${encodeURIComponent(publisherId)}`;
  const returnTo = `/login?returnTo=${encodeURIComponent(`${publisherHref}/signing-keys`)}`;
  const session = await getSession();
  if (session.state === "anonymous") redirect(returnTo);

  const [publisher, keys] =
    session.state === "authenticated"
      ? await Promise.all([getPublisher(organizationId, publisherId), listSigningKeys(organizationId, publisherId)])
      : [{ state: "unavailable" } as const, { state: "unavailable" } as const];
  if (publisher.state === "anonymous" || keys.state === "anonymous") redirect(returnTo);
  if (publisher.state === "not_found" || keys.state === "not_found") notFound();

  return (
    <>
      {session.state === "authenticated" && <SignedInHeader />}
      <main className="shell">
        <header className="header">
          <div>
            <p className="eyebrow">
              <Link href={`/organizations/${encodeURIComponent(organizationId)}`}>Organization</Link> · <Link href={publisherHref}>Publisher</Link> · Signing
              keys
            </p>
            <h1 data-testid="publisher-name">{publisher.state === "ok" ? publisher.data.displayName : "Publisher"}</h1>
          </div>
          {session.state === "authenticated" && <AccountBar email={session.session.email} />}
        </header>
        {publisher.state !== "ok" || keys.state !== "ok" ? (
          <OrganizationsUnavailable />
        ) : (
          <div className="stack">
            <section className="card" aria-labelledby="publisher-heading">
              <h2 id="publisher-heading">
                <code data-testid="publisher-handle">{publisher.data.handle}</code> <StateBadge state={publisher.data.state} />
              </h2>
              <dl>
                <div className="row">
                  <dt>Publisher ID</dt>
                  <dd>
                    <CanonicalId value={publisher.data.canonicalId} label="publisher ID" testId="publisher-id" />
                  </dd>
                </div>
              </dl>
            </section>
            <SigningKeyList organizationId={organizationId} publisherId={publisher.data.id} keys={keys.data.keys} />
          </div>
        )}
      </main>
    </>
  );
}
