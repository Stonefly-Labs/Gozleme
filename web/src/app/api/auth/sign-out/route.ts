import { forwardToApi } from "@/lib/api";
import { crossSiteForbidden, isSameOrigin } from "@/lib/origin";

export const dynamic = "force-dynamic";

/** Same-origin only (CSRF layer 1); forwards to `POST /api/v1/auth/sign-out` and relays the session cookie. */
export async function POST(request: Request): Promise<Response> {
  if (!isSameOrigin(request)) return crossSiteForbidden();
  return forwardToApi(request, "/api/v1/auth/sign-out");
}
