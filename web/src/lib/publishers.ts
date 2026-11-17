import { type Fetched, get, segment } from "@/lib/organizations";

/** Publishers API v1 shapes (docs/specs/005-publisher-identities/contracts/publishers-api.openapi.yaml). */
export type PublisherState = "active" | "suspended";

export interface Publisher {
  /** Opaque UUID used in URLs. */
  id: string;
  /** `gz://<origin>/pub/<id>`: permanent. */
  publisherId: string;
  origin: string;
  /** The owning organization's opaque id, shown on confirmation (FR-009). */
  organizationId: string;
  handle: string;
  displayName: string;
  state: PublisherState;
  createdAt: string;
  updatedAt: string;
}

export interface Capability {
  id: string;
  /** `gz://<origin>/cap/<id>`: permanent, never embeds a name. */
  canonicalId: string;
  origin: string;
  publisherId: string;
  /** `<handle>/<name>`: reserved to this capability forever. */
  alias: string;
  name: string;
  displayName: string;
  state: PublisherState;
  createdAt: string;
  updatedAt: string;
}

export const HANDLE_UNAVAILABLE = "That handle isn't available.";
export const NAME_CONFLICT = "That name is already used under this publisher.";
export const SUSPENDED = "This publisher is suspended. Its names and identifiers are frozen.";
export const RATE_LIMITED = "Too many registrations. Try again later.";

/** The namespace rule (contracts/namespace.md), shown as help text. Input is sent exactly as typed. */
export const HANDLE_RULE = "2–39 lowercase letters, digits and single hyphens, starting with a letter and not ending with a hyphen.";
export const NAME_RULE = "2–64 lowercase letters, digits and single hyphens, starting with a letter and not ending with a hyphen.";

/** The API's message for one field of a `400 validation_failed`, if any. */
export function fieldError(json: unknown, field: string): string | null {
  const fields = (json as { fields?: Record<string, string[]> } | null)?.fields;
  return fields?.[field]?.[0] ?? null;
}

/** The `error` code of a JSON error body, if any. */
export const errorCode = (json: unknown) => (json as { error?: string } | null)?.error ?? null;

export const publishersPath = (organizationId: string) => `/api/v1/organizations/${segment(organizationId)}/publishers`;
export const publisherPath = (organizationId: string, publisherId: string) => `${publishersPath(organizationId)}/${segment(publisherId)}`;
export const capabilitiesPath = (organizationId: string, publisherId: string) => `${publisherPath(organizationId, publisherId)}/capabilities`;

/** Server-side reads for pages, forwarding the session cookie like `getSession()`. */
export const listPublishers = (organizationId: string, fetchImpl: typeof fetch = fetch): Promise<Fetched<{ origin: string; publishers: Publisher[] }>> =>
  get(publishersPath(organizationId), fetchImpl);

export const getPublisher = (organizationId: string, publisherId: string, fetchImpl: typeof fetch = fetch): Promise<Fetched<Publisher>> =>
  get(publisherPath(organizationId, publisherId), fetchImpl);

export const listCapabilities = (
  organizationId: string,
  publisherId: string,
  fetchImpl: typeof fetch = fetch,
): Promise<Fetched<{ origin: string; capabilities: Capability[] }>> => get(capabilitiesPath(organizationId, publisherId), fetchImpl);
