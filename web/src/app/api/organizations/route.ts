import { forwardToApi } from "@/lib/api";
import { crossSiteForbidden, isSameOrigin } from "@/lib/origin";

export const dynamic = "force-dynamic";

/** Forwards to `GET /api/v1/organizations` (the caller's organizations and active context). */
export async function GET(request: Request): Promise<Response> {
  return forwardToApi(request, "/api/v1/organizations");
}

/** Same-origin only (CSRF layer 1); forwards to `POST /api/v1/organizations`. */
export async function POST(request: Request): Promise<Response> {
  if (!isSameOrigin(request)) return crossSiteForbidden();
  return forwardToApi(request, "/api/v1/organizations");
}
