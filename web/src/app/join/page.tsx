import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { AccountBar } from "@/components/AccountBar";
import { OrganizationsUnavailable } from "@/components/OrganizationsUnavailable";
import { RedeemInvitationForm } from "@/components/RedeemInvitationForm";
import { SignedInHeader } from "@/components/SignedInHeader";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";
export const metadata: Metadata = { title: "Join an organization — Gözleme" };

/** Redeem a single-use invitation code (FR-009); signed-in only. Codes never appear in the URL. */
export default async function JoinPage() {
  const session = await getSession();
  if (session.state === "anonymous") redirect("/login?returnTo=%2Fjoin");
  return (
    <>
      {session.state === "authenticated" && <SignedInHeader />}
      <main className="shell shell-narrow">
        <header className="header">
          <div>
            <p className="eyebrow">Gözleme · AI capability registry</p>
            <h1>Join an organization</h1>
          </div>
        </header>
        {session.state === "authenticated" ? (
          <>
            <AccountBar email={session.session.email} />
            <RedeemInvitationForm />
          </>
        ) : (
          <OrganizationsUnavailable />
        )}
      </main>
    </>
  );
}
