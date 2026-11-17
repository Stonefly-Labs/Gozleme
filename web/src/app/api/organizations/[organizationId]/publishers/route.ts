import { forwardToApi } from "@/lib/api";
import { crossSiteForbidden, isSameOrigin } from "@/lib/origin";
import { publishersPath } from "@/lib/publishers";

export const dynamic = "force-dynamic";

/** Same-origin only; forwards a registration to `POST …/{organizationId}/publishers` (GOZ-0005). */
export async function POST(request: Request, { params }: { params: Promise<{ organizationId: string }> }): Promise<Response> {
  if (!isSameOrigin(request)) return crossSiteForbidden();
  const { organizationId } = await params;
  return forwardToApi(request, publishersPath(organizationId));
}
