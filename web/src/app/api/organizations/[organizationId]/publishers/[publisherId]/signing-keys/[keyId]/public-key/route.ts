import { forwardBytesFromApi } from "@/lib/api";
import { segment } from "@/lib/organizations";
import { apiSigningKeysPath } from "@/lib/revisions-server";

export const dynamic = "force-dynamic";

/** Forwards the SPKI PEM download `GET …/signing-keys/{keyId}/public-key` (GOZ-0007) with its headers unchanged. */
export async function GET(request: Request, { params }: { params: Promise<{ organizationId: string; publisherId: string; keyId: string }> }): Promise<Response> {
  const { organizationId, publisherId, keyId } = await params;
  return forwardBytesFromApi(request, `${apiSigningKeysPath(organizationId, publisherId)}/${segment(keyId)}/public-key`);
}
