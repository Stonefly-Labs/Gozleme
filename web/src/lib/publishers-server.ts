import { type Fetched, get, segment } from "@/lib/organizations";
import type { Capability, Publisher } from "@/lib/publishers";

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
