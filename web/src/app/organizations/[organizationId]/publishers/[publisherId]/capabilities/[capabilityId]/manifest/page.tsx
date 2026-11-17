import type { Metadata } from "next";
import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { AccountBar } from "@/components/AccountBar";
import { CanonicalId } from "@/components/CanonicalId";
import { ManifestValidator } from "@/components/ManifestValidator";
import { OrganizationsUnavailable } from "@/components/OrganizationsUnavailable";
import { SignedInHeader } from "@/components/SignedInHeader";
import { StateBadge } from "@/components/StateBadge";
import { MANIFEST_MESSAGES } from "@/lib/manifests";
import { getOrganization } from "@/lib/organizations";
import { getCapability, getPublisher } from "@/lib/publishers-server";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";
// A fixed title: capability names stay out of document titles and browser telemetry.
export const metadata: Metadata = { title: "Validate manifest — Gözleme" };

/**
 * Validate a manifest for one capability (GOZ-0006). The API re-checks membership on every load, so non-members and
 * unknown, malformed or foreign ids all get the same "not found" page. Reached directly without `manifest.validate`,
 * or while the publisher is suspended, the page explains why instead of showing the form; the API still decides
 * every validation.
 */
export default async function ManifestPage({
  params,
}: {
  params: Promise<{ organizationId: string; publisherId: string; capabilityId: string }>;
}) {
  const { organizationId, publisherId, capabilityId } = await params;
  const publisherHref = `/organizations/${encodeURIComponent(organizationId)}/publishers/${encodeURIComponent(publisherId)}`;
  const returnTo = `/login?returnTo=${encodeURIComponent(`${publisherHref}/capabilities/${encodeURIComponent(capabilityId)}/manifest`)}`;
  const session = await getSession();
  if (session.state === "anonymous") redirect(returnTo);

  const [organization, publisher, capability] =
    session.state === "authenticated"
      ? await Promise.all([getOrganization(organizationId), getPublisher(organizationId, publisherId), getCapability(organizationId, publisherId, capabilityId)])
      : [{ state: "unavailable" } as const, { state: "unavailable" } as const, { state: "unavailable" } as const];
  if (organization.state === "anonymous" || publisher.state === "anonymous" || capability.state === "anonymous") redirect(returnTo);
  if (organization.state === "not_found" || publisher.state === "not_found" || capability.state === "not_found") notFound();

  const ready = organization.state === "ok" && publisher.state === "ok" && capability.state === "ok";
  const permitted = organization.state === "ok" && organization.data.permissions.includes("manifest.validate");
  const active = publisher.state === "ok" && publisher.data.state === "active";
  return (
    <>
      {session.state === "authenticated" && <SignedInHeader />}
      <main className="shell">
        <header className="header">
          <div>
            <p className="eyebrow">
              <Link href={`/organizations/${encodeURIComponent(organizationId)}`}>Organization</Link> · <Link href={publisherHref}>Publisher</Link> ·
              Validate manifest
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
                <code data-testid="capability-alias">{capability.data.alias}</code> <StateBadge state={capability.data.state} />
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
            {!active ? (
              <div className="notice" role="status" data-testid="suspended-notice">
                <p>{MANIFEST_MESSAGES.suspended}</p>
              </div>
            ) : !permitted ? (
              <p role="alert" className="form-error card" data-testid="permission-notice">
                {MANIFEST_MESSAGES.forbidden}
              </p>
            ) : (
              <ManifestValidator
                organizationId={organizationId}
                publisherId={publisherId}
                capabilityId={capability.data.id}
                canonicalId={capability.data.canonicalId}
              />
            )}
          </div>
        )}
      </main>
    </>
  );
}
