import { headers } from "next/headers";
import { apiBaseUrl, apiHeaders } from "@/lib/api";

export interface Session {
  userId: string;
  email: string;
  emailVerified: boolean;
  expiresAt: string;
}

export type SessionState =
  | { state: "authenticated"; session: Session }
  | { state: "anonymous" }
  /** Session could not be verified (database or API unavailable): never treated as signed in. */
  | { state: "unavailable" };

/** Server-side session check for pages; the API is authoritative (FR-012). */
export async function getSession(fetchImpl: typeof fetch = fetch): Promise<SessionState> {
  const incoming = await headers();
  if (!incoming.get("cookie")?.includes("gozleme_session=")) return { state: "anonymous" };
  try {
    const res = await fetchImpl(new URL("/api/v1/auth/session", apiBaseUrl()), {
      headers: apiHeaders({ headers: incoming }, false),
      cache: "no-store",
      signal: AbortSignal.timeout(3000),
    });
    if (res.status === 401) return { state: "anonymous" };
    if (!res.ok) return { state: "unavailable" };
    return { state: "authenticated", session: (await res.json()) as Session };
  } catch {
    return { state: "unavailable" };
  }
}

export async function getAuthOptions(fetchImpl: typeof fetch = fetch): Promise<{ registrationEnabled: boolean }> {
  try {
    const res = await fetchImpl(new URL("/api/v1/auth/options", apiBaseUrl()), { cache: "no-store", signal: AbortSignal.timeout(3000) });
    if (res.ok) return { registrationEnabled: ((await res.json()) as { registrationEnabled: boolean }).registrationEnabled };
  } catch {
    // fall through: offer no registration link while the API cannot confirm it is open
  }
  return { registrationEnabled: false };
}
