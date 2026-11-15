import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { RegisterForm, RegistrationClosed } from "@/components/RegisterForm";
import { getAuthOptions, getSession } from "@/lib/session";

export const dynamic = "force-dynamic";
export const metadata: Metadata = { title: "Create account — Gözleme" };

export default async function RegisterPage() {
  if ((await getSession()).state === "authenticated") redirect("/");
  const { registrationEnabled } = await getAuthOptions();
  return (
    <main className="shell shell-narrow">
      <header className="header">
        <div>
          <p className="eyebrow">Gözleme · AI capability registry</p>
          <h1>Create account</h1>
        </div>
      </header>
      {registrationEnabled ? <RegisterForm /> : <RegistrationClosed />}
    </main>
  );
}
