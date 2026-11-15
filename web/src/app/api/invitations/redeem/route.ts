import { forwardToApi } from "@/lib/api";
import { crossSiteForbidden, isSameOrigin } from "@/lib/origin";

export const dynamic = "force-dynamic";

/** Same-origin only; forwards to `POST /api/v1/invitations/redeem`. The code travels in the body, never the URL. */
export async function POST(request: Request): Promise<Response> {
  if (!isSameOrigin(request)) return crossSiteForbidden();
  return forwardToApi(request, "/api/v1/invitations/redeem");
}
