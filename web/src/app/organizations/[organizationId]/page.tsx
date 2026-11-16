import type { Metadata } from "next";
import { notFound, redirect } from "next/navigation";
import { AccountBar } from "@/components/AccountBar";
import { InvitationPanel } from "@/components/InvitationPanel";
import { MemberList } from "@/components/MemberList";
import { OrganizationsUnavailable } from "@/components/OrganizationsUnavailable";
import { RenameOrganizationForm } from "@/components/RenameOrganizationForm";
import { SignedInHeader } from "@/components/SignedInHeader";
import { getOrganization, listInvitations, listMembers } from "@/lib/organizations";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";
// A fixed title: display names stay out of document titles and browser telemetry.
export const metadata: Metadata = { title: "Organization — Gözleme" };

/**
 * One organization (FR-003, FR-012, FR-014). Membership is re-checked by the API on every load, so a
 * non-member, an unknown id and a malformed id all get the same "Organization not found" page (FR-006).
 */
export default async function OrganizationPage({ params }: { params: Promise<{ organizationId: string }> }) {
  const { organizationId } = await params;
  const returnTo = `/login?returnTo=${encodeURIComponent(`/organizations/${encodeURIComponent(organizationId)}`)}`;
  const session = await getSession();
  if (session.state === "anonymous") redirect(returnTo);

  const [organization, members, invitations] =
    session.state === "authenticated"
      ? await Promise.all([getOrganization(organizationId), listMembers(organizationId), listInvitations(organizationId)])
      : [{ state: "unavailable" } as const, { state: "unavailable" } as const, { state: "unavailable" } as const];
  if (organization.state === "anonymous") redirect(returnTo);
  if (organization.state === "not_found" || members.state === "not_found" || invitations.state === "not_found") notFound();

  const ready = organization.state === "ok" && members.state === "ok" && invitations.state === "ok";
  return (
    <>
      {session.state === "authenticated" && <SignedInHeader />}
      <main className="shell">
        <header className="header">
          <div>
            <p className="eyebrow">Organization</p>
            <h1 data-testid="organization-name">{organization.state === "ok" ? organization.data.displayName : "Organization"}</h1>
          </div>
          {session.state === "authenticated" && <AccountBar email={session.session.email} />}
        </header>
        {!ready ? (
          <OrganizationsUnavailable />
        ) : (
          <div className="stack">
            <RenameOrganizationForm key={organization.data.displayName} organizationId={organization.data.id} displayName={organization.data.displayName} />
            <MemberList organizationId={organization.data.id} members={members.data.members} />
            <InvitationPanel organizationId={organization.data.id} invitations={invitations.data.invitations} members={members.data.members} />
          </div>
        )}
      </main>
    </>
  );
}
