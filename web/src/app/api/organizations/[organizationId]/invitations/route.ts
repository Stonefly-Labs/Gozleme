import { forwardToApi } from "@/lib/api";
import { crossSiteForbidden, isSameOrigin } from "@/lib/origin";
import { segment } from "@/lib/organizations";

export const dynamic = "force-dynamic";

type Params = { params: Promise<{ organizationId: string }> };

/** Forwards to `GET …/invitations` (open invitations; codes are never returned again). */
export async function GET(request: Request, { params }: Params): Promise<Response> {
  const { organizationId } = await params;
  return forwardToApi(request, `/api/v1/organizations/${segment(organizationId)}/invitations`);
}

/** Same-origin only; forwards to `POST …/invitations` (the response carries the code once). */
export async function POST(request: Request, { params }: Params): Promise<Response> {
  if (!isSameOrigin(request)) return crossSiteForbidden();
  const { organizationId } = await params;
  return forwardToApi(request, `/api/v1/organizations/${segment(organizationId)}/invitations`);
}
