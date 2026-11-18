import type { Metadata } from "next";
import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { AccountBar } from "@/components/AccountBar";
import { CanonicalId } from "@/components/CanonicalId";
import { CapabilityList } from "@/components/CapabilityList";
import { CreateCapabilityForm } from "@/components/CreateCapabilityForm";
import { OrganizationsUnavailable } from "@/components/OrganizationsUnavailable";
import { RenameDisplayNameForm } from "@/components/RenameDisplayNameForm";
import { SignedInHeader } from "@/components/SignedInHeader";
import { StateBadge } from "@/components/StateBadge";
import { forbiddenText } from "@/lib/client";
import { getOrganization } from "@/lib/organizations";
import { SUSPENDED } from "@/lib/publishers";
import { getPublisher, listCapabilities } from "@/lib/publishers-server";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";
// A fixed title: handles and display names stay out of document titles and browser telemetry.
export const metadata: Metadata = { title: "Publisher — Gözleme" };

/**
 * One publisher and its capabilities (GOZ-0005). The API re-checks membership on every load, so a non-member,
 * an unknown, malformed or foreign id all get the same "not found" page (FR-024). Write controls follow the
 * caller's permissions and disappear while the publisher is suspended; the API still decides every request.
 */
export default async function PublisherPage({
  params,
  searchParams,
}: {
  params: Promise<{ organizationId: string; publisherId: string }>;
  searchParams: Promise<{ notice?: string | string[] }>;
}) {
  const { organizationId, publisherId } = await params;
  const { notice } = await searchParams;
  const here = `/organizations/${encodeURIComponent(organizationId)}/publishers/${encodeURIComponent(publisherId)}`;
  const returnTo = `/login?returnTo=${encodeURIComponent(here)}`;
  const session = await getSession();
  if (session.state === "anonymous") redirect(returnTo);

  const [organization, publisher, capabilities] =
    session.state === "authenticated"
      ? await Promise.all([getOrganization(organizationId), getPublisher(organizationId, publisherId), listCapabilities(organizationId, publisherId)])
      : [{ state: "unavailable" } as const, { state: "unavailable" } as const, { state: "unavailable" } as const];
  if (organization.state === "anonymous" || publisher.state === "anonymous") redirect(returnTo);
  if (organization.state === "not_found" || publisher.state === "not_found" || capabilities.state === "not_found") notFound();

  const ready = organization.state === "ok" && publisher.state === "ok" && capabilities.state === "ok";
  const permissions = organization.state === "ok" ? organization.data.permissions : [];
  const active = publisher.state === "ok" && publisher.data.state === "active";
  return (
    <>
      {session.state === "authenticated" && <SignedInHeader />}
      <main className="shell">
        <header className="header">
          <div>
            <p className="eyebrow">
              <Link href={`/organizations/${encodeURIComponent(organizationId)}`}>Organization</Link> · Publisher
            </p>
            <h1 data-testid="publisher-name">{publisher.state === "ok" ? publisher.data.displayName : "Publisher"}</h1>
          </div>
          {session.state === "authenticated" && <AccountBar email={session.session.email} />}
        </header>
        {!ready ? (
          <OrganizationsUnavailable />
        ) : (
          <div className="stack">
            {notice === "forbidden" && (
              <p role="alert" className="form-error card" data-testid="permission-notice">
                {forbiddenText}
              </p>
            )}
            {!active && (
              <div className="notice" role="status" data-testid="suspended-notice">
                <p>{SUSPENDED}</p>
              </div>
            )}
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
                <div className="row">
                  <dt>Organization</dt>
                  <dd>
                    <code>{publisher.data.organizationId}</code>
                  </dd>
                </div>
                <div className="row">
                  <dt>Registry origin</dt>
                  <dd>
                    <code>{publisher.data.origin}</code>
                  </dd>
                </div>
              </dl>
              {active && permissions.includes("publisher.rename") && (
                <RenameDisplayNameForm
                  key={publisher.data.displayName}
                  organizationId={organizationId}
                  path={`/api/organizations/${encodeURIComponent(organizationId)}/publishers/${encodeURIComponent(publisher.data.id)}`}
                  flow="publisher_rename"
                  label="Publisher display name"
                  displayName={publisher.data.displayName}
                  inputId="publisher-rename"
                />
              )}
            </section>
            {active && permissions.includes("capability.create") && <CreateCapabilityForm organizationId={organizationId} publisherId={publisher.data.id} />}
            <CapabilityList
              organizationId={organizationId}
              publisherId={publisher.data.id}
              capabilities={capabilities.data.capabilities}
              canRename={active && permissions.includes("capability.rename")}
              canValidate={active && permissions.includes("manifest.validate")}
            />
          </div>
        )}
      </main>
    </>
  );
}
