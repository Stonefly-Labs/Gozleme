import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { SignInForm } from "@/components/SignInForm";
import { safeReturnTo } from "@/lib/returnTo";
import { getAuthOptions, getSession } from "@/lib/session";

export const dynamic = "force-dynamic";
export const metadata: Metadata = { title: "Sign in — Gözleme" };

/** Local accounts only (FR-028): no external or single-sign-on option is offered. */
export default async function LoginPage({ searchParams }: { searchParams: Promise<{ returnTo?: string | string[] }> }) {
  const raw = (await searchParams).returnTo;
  const returnTo = safeReturnTo(Array.isArray(raw) ? raw[0] : raw);
  if ((await getSession()).state === "authenticated") redirect(returnTo);
  const { registrationEnabled } = await getAuthOptions();
  return (
    <main className="shell shell-narrow">
      <header className="header">
        <div>
          <p className="eyebrow">Gözleme · AI capability registry</p>
          <h1>Sign in</h1>
        </div>
      </header>
      <SignInForm returnTo={returnTo} registrationEnabled={registrationEnabled} />
    </main>
  );
}
