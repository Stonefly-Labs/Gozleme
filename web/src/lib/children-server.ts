import type { ChildDetail, ChildList, GenerationList, RevisionChildList } from "@/lib/children";
import { type Fetched, get, segment } from "@/lib/organizations";
import { capabilitiesPath } from "@/lib/publishers-server";
import { apiRevisionsPath } from "@/lib/revisions-server";

export const apiChildrenPath = (organizationId: string, publisherId: string, capabilityId: string) =>
  `${capabilitiesPath(organizationId, publisherId)}/${segment(capabilityId)}/children`;

export const apiRevisionChildrenPath = (organizationId: string, publisherId: string, capabilityId: string, revisionId: string) =>
  `${apiRevisionsPath(organizationId, publisherId, capabilityId)}/${segment(revisionId)}/children`;

export const apiProjectionsPath = (organizationId: string, publisherId: string, capabilityId: string, revisionId: string) =>
  `${apiRevisionsPath(organizationId, publisherId, capabilityId)}/${segment(revisionId)}/projections`;

/** Only integer `after` and `limit` are forwarded (as for revisions); anything else is dropped. */
export function pageQuery(search: URLSearchParams): string {
  const query = new URLSearchParams();
  for (const name of ["after", "limit"]) {
    const value = search.get(name);
    if (value !== null && /^\d{1,9}$/.test(value)) query.set(name, value);
  }

  return query.size > 0 ? `?${query}` : "";
}

const after = (value: string | null) => (value === null ? "" : `?after=${value}`);

/** Server-side reads for pages, forwarding the session cookie like `getSession()`. */
export const listChildren = (organizationId: string, publisherId: string, capabilityId: string, cursor: string | null, fetchImpl: typeof fetch = fetch): Promise<Fetched<ChildList>> =>
  get(`${apiChildrenPath(organizationId, publisherId, capabilityId)}${after(cursor)}`, fetchImpl);

export const getChild = (
  organizationId: string,
  publisherId: string,
  capabilityId: string,
  childId: string,
  cursor: string | null,
  fetchImpl: typeof fetch = fetch,
): Promise<Fetched<ChildDetail>> => get(`${apiChildrenPath(organizationId, publisherId, capabilityId)}/${segment(childId)}${after(cursor)}`, fetchImpl);

export const listRevisionChildren = (
  organizationId: string,
  publisherId: string,
  capabilityId: string,
  revisionId: string,
  cursor: string | null,
  fetchImpl: typeof fetch = fetch,
): Promise<Fetched<RevisionChildList>> =>
  get(`${apiRevisionChildrenPath(organizationId, publisherId, capabilityId, revisionId)}?limit=100${cursor === null ? "" : `&after=${cursor}`}`, fetchImpl);

export const listGenerations = (
  organizationId: string,
  publisherId: string,
  capabilityId: string,
  revisionId: string,
  fetchImpl: typeof fetch = fetch,
): Promise<Fetched<GenerationList>> => get(apiProjectionsPath(organizationId, publisherId, capabilityId, revisionId), fetchImpl);
