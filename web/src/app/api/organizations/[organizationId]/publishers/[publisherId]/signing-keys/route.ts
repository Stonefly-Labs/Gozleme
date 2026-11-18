import { forwardToApi } from "@/lib/api";
import { apiSigningKeysPath } from "@/lib/revisions-server";

export const dynamic = "force-dynamic";

/** Forwards to `GET …/publishers/{publisherId}/signing-keys` (GOZ-0007): public parts only. */
export async function GET(request: Request, { params }: { params: Promise<{ organizationId: string; publisherId: string }> }): Promise<Response> {
  const { organizationId, publisherId } = await params;
  return forwardToApi(request, apiSigningKeysPath(organizationId, publisherId));
}
