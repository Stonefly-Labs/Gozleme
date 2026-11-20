import { forwardBytesFromApi } from "@/lib/api";
import { apiProjectionsPath } from "@/lib/children-server";
import { segment } from "@/lib/organizations";

export const dynamic = "force-dynamic";

type Params = { params: Promise<{ organizationId: string; publisherId: string; capabilityId: string; revisionId: string; generationId: string }> };

/** Forwards one generation's canonical projection bytes with its attachment headers unchanged (GOZ-0008 FR-010). */
export async function GET(request: Request, { params }: Params): Promise<Response> {
  const { organizationId, publisherId, capabilityId, revisionId, generationId } = await params;
  return forwardBytesFromApi(request, `${apiProjectionsPath(organizationId, publisherId, capabilityId, revisionId)}/${segment(generationId)}`);
}
