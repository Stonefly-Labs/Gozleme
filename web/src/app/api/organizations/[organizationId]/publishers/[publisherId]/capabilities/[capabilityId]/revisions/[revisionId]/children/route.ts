import { forwardToApi } from "@/lib/api";
import { apiRevisionChildrenPath, pageQuery } from "@/lib/children-server";

export const dynamic = "force-dynamic";

/** Forwards to `GET …/revisions/{revisionId}/children` (GOZ-0008): the revision's entries in projection order. */
export async function GET(
  request: Request,
  { params }: { params: Promise<{ organizationId: string; publisherId: string; capabilityId: string; revisionId: string }> },
): Promise<Response> {
  const { organizationId, publisherId, capabilityId, revisionId } = await params;
  return forwardToApi(request, `${apiRevisionChildrenPath(organizationId, publisherId, capabilityId, revisionId)}${pageQuery(new URL(request.url).searchParams)}`);
}
