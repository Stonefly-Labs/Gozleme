import { forwardToApi } from "@/lib/api";
import { apiProjectionsPath } from "@/lib/children-server";

export const dynamic = "force-dynamic";

/** Forwards to `GET …/revisions/{revisionId}/projections` (GOZ-0008): the revision's projection generations. */
export async function GET(
  request: Request,
  { params }: { params: Promise<{ organizationId: string; publisherId: string; capabilityId: string; revisionId: string }> },
): Promise<Response> {
  const { organizationId, publisherId, capabilityId, revisionId } = await params;
  return forwardToApi(request, apiProjectionsPath(organizationId, publisherId, capabilityId, revisionId));
}
