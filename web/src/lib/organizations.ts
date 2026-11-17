import { headers } from "next/headers";
import { apiBaseUrl, apiHeaders } from "@/lib/api";

/** Organizations API v1 shapes (contracts/organizations-api.openapi.yaml; roles from GOZ-0004 rbac-api.openapi.yaml). */
export interface Organization {
  id: string;
  displayName: string;
  createdAt: string;
}

/** Built-in roles (GOZ-0004 docs/architecture/contracts/organization-permissions.md). Every member holds `member`. */
export const ROLES = ["admin", "approver", "member", "publisher", "reviewer"] as const;
export type Role = (typeof ROLES)[number];

/** `GET /organizations/{id}`: the caller's own roles and the actions they permit. */
export interface OrganizationDetail extends Organization {
  roles: Role[];
  permissions: string[];
}

export interface OrganizationList {
  organizations: Organization[];
  activeOrganizationId: string | null;
}

export interface Member {
  userId: string;
  email: string;
  joinedAt: string;
  joinedVia: "creator" | "invitation";
  isSelf: boolean;
  roles: Role[];
}

export interface Invitation {
  id: string;
  createdAt: string;
  expiresAt: string;
  createdBy: string | null;
}

export type Fetched<T> =
  | { state: "ok"; data: T }
  /** Not a member, unknown or malformed id: the API does not say which (FR-006). */
  | { state: "not_found" }
  | { state: "anonymous" }
  /** API or database unavailable: show the honest degraded view, never stale data. */
  | { state: "unavailable" };

/**
 * Path segment for an id taken from the URL; the web tier never interprets ids, the API decides.
 * Dot segments (`.`, `..`, even percent-encoded) would be normalised away by URL parsing and retarget
 * the call, so they become a value the API can only answer with "not found".
 */
export const segment = (id: string) => (/^\.+$/.test(id) ? "not-found" : encodeURIComponent(id));

/** One server-side read; also used by the publisher pages (GOZ-0005). */
export async function get<T>(path: string, fetchImpl: typeof fetch): Promise<Fetched<T>> {
  try {
    const res = await fetchImpl(new URL(path, apiBaseUrl()), {
      headers: apiHeaders({ headers: await headers() }, false),
      cache: "no-store",
      signal: AbortSignal.timeout(3000),
    });
    if (res.status === 401) return { state: "anonymous" };
    if (res.status === 404) return { state: "not_found" };
    if (!res.ok) return { state: "unavailable" };
    return { state: "ok", data: (await res.json()) as T };
  } catch {
    return { state: "unavailable" };
  }
}

/** Server-side reads for pages, forwarding the session cookie like `getSession()`. */
export const listOrganizations = (fetchImpl: typeof fetch = fetch) => get<OrganizationList>("/api/v1/organizations", fetchImpl);

export const getOrganization = (id: string, fetchImpl: typeof fetch = fetch) => get<OrganizationDetail>(`/api/v1/organizations/${segment(id)}`, fetchImpl);

export const listMembers = (id: string, fetchImpl: typeof fetch = fetch) =>
  get<{ members: Member[] }>(`/api/v1/organizations/${segment(id)}/members`, fetchImpl);

/** Admin-only (`invitation.list`); a member's page never asks, so it never sees a 403 here. */
export const listInvitations = (id: string, fetchImpl: typeof fetch = fetch) =>
  get<{ invitations: Invitation[] }>(`/api/v1/organizations/${segment(id)}/invitations`, fetchImpl);
