import { forwardToApi } from "@/lib/api";
import { apiChildrenPath, pageQuery } from "@/lib/children-server";

export const dynamic = "force-dynamic";

/** Forwards to `GET …/capabilities/{capabilityId}/children` (GOZ-0008), passing only integer `after` and `limit`. */
export async function GET(request: Request, { params }: { params: Promise<{ organizationId: string; publisherId: string; capabilityId: string }> }): Promise<Response> {
  const { organizationId, publisherId, capabilityId } = await params;
  return forwardToApi(request, `${apiChildrenPath(organizationId, publisherId, capabilityId)}${pageQuery(new URL(request.url).searchParams)}`);
}
