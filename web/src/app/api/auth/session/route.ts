import { forwardToApi } from "@/lib/api";

export const dynamic = "force-dynamic";

/** Forwards to `GET /api/v1/auth/session`. */
export async function GET(request: Request): Promise<Response> {
  return forwardToApi(request, "/api/v1/auth/session");
}
