import { type Fetched, get, segment } from "@/lib/organizations";
import { capabilitiesPath, publisherPath } from "@/lib/publishers-server";
import type { RevisionDetail, RevisionList, SigningKeyList } from "@/lib/revisions";

export const apiRevisionsPath = (organizationId: string, publisherId: string, capabilityId: string) =>
  `${capabilitiesPath(organizationId, publisherId)}/${segment(capabilityId)}/revisions`;

export const apiSigningKeysPath = (organizationId: string, publisherId: string) => `${publisherPath(organizationId, publisherId)}/signing-keys`;

/** Only a non-negative integer cursor is forwarded; anything else starts at the beginning. */
export const cursor = (after: string | string[] | undefined) => (typeof after === "string" && /^\d{1,9}$/.test(after) ? after : null);

/** Server-side reads for pages, forwarding the session cookie like `getSession()`. */
export const listRevisions = (
  organizationId: string,
  publisherId: string,
  capabilityId: string,
  after: string | null,
  fetchImpl: typeof fetch = fetch,
): Promise<Fetched<RevisionList>> =>
  get(`${apiRevisionsPath(organizationId, publisherId, capabilityId)}${after === null ? "" : `?after=${after}`}`, fetchImpl);

export const getRevision = (
  organizationId: string,
  publisherId: string,
  capabilityId: string,
  revisionId: string,
  fetchImpl: typeof fetch = fetch,
): Promise<Fetched<RevisionDetail>> => get(`${apiRevisionsPath(organizationId, publisherId, capabilityId)}/${segment(revisionId)}`, fetchImpl);

export const listSigningKeys = (organizationId: string, publisherId: string, fetchImpl: typeof fetch = fetch): Promise<Fetched<SigningKeyList>> =>
  get(apiSigningKeysPath(organizationId, publisherId), fetchImpl);
