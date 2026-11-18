import { forwardBytesFromApi } from "@/lib/api";
import { segment } from "@/lib/organizations";
import { DOWNLOAD_PARTS } from "@/lib/revisions";
import { apiRevisionsPath } from "@/lib/revisions-server";

export const dynamic = "force-dynamic";

type Params = { params: Promise<{ organizationId: string; publisherId: string; capabilityId: string; revisionId: string; part: string }> };

/**
 * Forwards one of the four downloads (`native`, `projection`, `statement`, `signature`) of a revision with its bytes
 * and headers unchanged (GOZ-0007 FR-011). Any other part is an empty 404 without calling the API.
 */
export async function GET(request: Request, { params }: Params): Promise<Response> {
  const { organizationId, publisherId, capabilityId, revisionId, part } = await params;
  if (!(DOWNLOAD_PARTS as readonly string[]).includes(part)) return new Response(null, { status: 404, headers: { "cache-control": "no-store" } });
  return forwardBytesFromApi(request, `${apiRevisionsPath(organizationId, publisherId, capabilityId)}/${segment(revisionId)}/${part}`);
}
