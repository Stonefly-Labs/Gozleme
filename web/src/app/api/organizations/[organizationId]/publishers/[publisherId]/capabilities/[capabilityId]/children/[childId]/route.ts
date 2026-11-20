import { forwardToApi } from "@/lib/api";
import { apiChildrenPath, pageQuery } from "@/lib/children-server";
import { segment } from "@/lib/organizations";

export const dynamic = "force-dynamic";

/** Forwards to `GET …/capabilities/{capabilityId}/children/{childId}` (GOZ-0008): the child and its history page. */
export async function GET(
  request: Request,
  { params }: { params: Promise<{ organizationId: string; publisherId: string; capabilityId: string; childId: string }> },
): Promise<Response> {
  const { organizationId, publisherId, capabilityId, childId } = await params;
  return forwardToApi(request, `${apiChildrenPath(organizationId, publisherId, capabilityId)}/${segment(childId)}${pageQuery(new URL(request.url).searchParams)}`);
}
