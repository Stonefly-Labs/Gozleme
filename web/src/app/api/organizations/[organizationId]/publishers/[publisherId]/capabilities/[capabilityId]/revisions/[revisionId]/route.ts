import { forwardToApi } from "@/lib/api";
import { segment } from "@/lib/organizations";
import { apiRevisionsPath } from "@/lib/revisions-server";

export const dynamic = "force-dynamic";

/** Forwards to `GET …/capabilities/{capabilityId}/revisions/{revisionId}` (GOZ-0007): the detail, verified now. */
export async function GET(
  request: Request,
  { params }: { params: Promise<{ organizationId: string; publisherId: string; capabilityId: string; revisionId: string }> },
): Promise<Response> {
  const { organizationId, publisherId, capabilityId, revisionId } = await params;
  return forwardToApi(request, `${apiRevisionsPath(organizationId, publisherId, capabilityId)}/${segment(revisionId)}`);
}
