import { redirect } from "next/navigation";
import { AccountBar } from "@/components/AccountBar";
import { StatusDashboard } from "@/components/StatusDashboard";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";

/**
 * Protected status dashboard (FR-021). The API decides: anonymous → sign-in; unverifiable (database
 * or API down) → the dashboard without any account or status data, which shows the outage honestly.
 */
export default async function Home() {
  const result = await getSession();
  if (result.state === "anonymous") redirect("/login?returnTo=%2F");
  return <StatusDashboard account={result.state === "authenticated" ? <AccountBar email={result.session.email} /> : undefined} />;
}
