/**
 * Signed revision v1 shapes (docs/specs/007-immutable-revisions/contracts/revision-api.openapi.yaml), the fixed proof
 * wording (research R17), verification reason messages and client-safe error messages. Mirrors
 * `src/Gozleme.Api/Revisions/RevisionContracts.cs`. Server-side reads live in `revisions-server.ts`.
 */
import type { EnvelopeSummary, Projection } from "@/lib/manifests";

export type VerificationStatus = "verified" | "failed";

/** The closed reason set, in the order the verifier checks them (contracts/attestation-statement-v1.md). */
export const VERIFICATION_REASONS = [
  "malformed_statement",
  "unknown_statement_version",
  "unknown_field",
  "unknown_algorithm",
  "unknown_key",
  "signature_invalid",
  "statement_mismatch",
  "native_digest_mismatch",
  "projection_digest_mismatch",
] as const;

export type VerificationReason = (typeof VERIFICATION_REASONS)[number];

export type KeyStatus = "active" | "retired";

export type RetiredReason = "lost" | "compromised";

export interface KeyStatusView {
  id: string;
  keyId: string;
  status: KeyStatus;
  retiredAt?: string;
  retiredReason?: RetiredReason;
}

export interface Verification {
  status: VerificationStatus;
  reason: VerificationReason | null;
  key: KeyStatusView;
}

export interface RevisionSummary {
  id: string;
  /** `gz://<origin>/cap/<id>`. */
  capabilityId: string;
  version: string;
  sequence: number;
  publishedAt: string;
  /** The publishing account's opaque ID. */
  publishedBy: string;
  nativeDigest: string;
  verification: Verification;
}

/** Revisions in publication order; `next` is the `after` value of the next page, or null at the end. No "latest". */
export interface RevisionList {
  publisherSuspended: boolean;
  revisions: RevisionSummary[];
  next: number | null;
}

export interface Attestation {
  statementVersion: number;
  /** The stored statement as an object; empty when its bytes are not a canonical statement. */
  statement: Record<string, unknown>;
  algorithm: string;
  keyId: string;
  proof: string;
}

export interface RevisionDetail extends RevisionSummary {
  /** `gz://<origin>/pub/<id>`. */
  publisherId: string;
  publisherSuspended: boolean;
  envelope: EnvelopeSummary;
  native: { format: string; formatVersion: string; encoding: string; document: string; digest: string; byteLength: number };
  projection: Projection;
  projectionDigest: string;
  adapter: { id: string; version: string };
  ruleCatalogueVersion: string;
  schemaBundleVersion: string;
  attestation: Attestation;
}

export interface SigningKey {
  id: string;
  keyId: string;
  algorithm: string;
  generation: number;
  createdAt: string;
  status: KeyStatus;
  retiredAt?: string;
  retiredReason?: RetiredReason;
  successorId?: string;
}

export interface SigningKeyList {
  publisherId: string;
  keys: SigningKey[];
}

/** The origin of a `gz://<origin>/…` canonical ID. */
export const originOf = (canonicalId: string) => canonicalId.replace(/^gz:\/\//, "").split("/")[0] ?? "";

/** What an attestation proves and does not prove: fixed wording, never composed from the statement (R17, FR-013). */
export const proofWording = (origin: string, publisherCanonicalId: string) =>
  `Signed by registry ${origin} for publisher ${publisherCanonicalId} with a registry-held key. ` +
  "This shows that this registry accepted the revision from an authorized publisher member. It is not a signature made by the publisher.";

export const VERIFICATION_MESSAGES: Record<VerificationReason, string> = {
  malformed_statement: "The stored statement is not a well-formed canonical statement.",
  unknown_statement_version: "The stored statement has a statement version this registry does not know.",
  unknown_field: "The stored statement has a missing or unknown field.",
  unknown_algorithm: "The stored statement names a signature algorithm this registry does not know.",
  unknown_key: "The stored statement names a key that is not this publisher's signing key.",
  signature_invalid: "The signature does not match the stored statement.",
  statement_mismatch: "The signed statement does not match the stored revision.",
  native_digest_mismatch: "The stored native document no longer matches its signed digest.",
  projection_digest_mismatch: "The stored projection no longer matches its signed digest.",
};

/** A failed revision is never shown as verified, whatever it was before (US2-AS4). */
export const NEVER_VERIFIED = "Do not rely on this revision: it is not shown as verified.";

export const reasonMessage = (reason: VerificationReason | null) =>
  reason !== null && Object.hasOwn(VERIFICATION_MESSAGES, reason) ? VERIFICATION_MESSAGES[reason] : "Verification failed.";

export const REVISION_MESSAGES = {
  version_exists: "This version is already published for this capability. Revisions never change; publish a new version instead.",
  forbidden: "You don't have permission to publish revisions for this capability. The publisher role is required.",
  suspended: "This publisher is suspended, so new revisions can't be published. Existing revisions stay readable.",
  rate_limited: "Too many publications. Wait a minute and try again.",
  unavailable: "Publishing is unavailable right now. Try again shortly.",
  not_found: "Capability not found.",
  too_large: "The request is larger than 1 MiB.",
  unsupported_media_type: "The request must be sent as application/json.",
  rejected: "The manifest was rejected when publishing. Validate it again to see the problems.",
} as const;

export const SUSPENDED_PUBLISHER = "This publisher is suspended. Its revisions stay readable and verifiable, and no new revision can be published.";

export const COMPROMISED_KEY =
  "The key that signed this revision was retired as compromised by a registry operator. The signature still checks, but Gözleme does not decide which earlier signatures are genuine.";

export const COMPROMISED_KEYS =
  "A registry operator retired a key of this publisher as compromised. Revisions it signed still check, but Gözleme does not decide which of them are genuine.";

export const NO_KEY_YET = "No key yet. The registry creates this publisher's signing key with its first publication.";

export const SUCCESSION_NOTE =
  "Signing keys are held by this registry. A registry operator can retire a key and add its successor as a local operator action; earlier revisions keep their signatures.";

const isMessageCode = (code: unknown): code is keyof typeof REVISION_MESSAGES => typeof code === "string" && Object.hasOwn(REVISION_MESSAGES, code);

/** The message for a publication answer that is not `201`, from its status and body. */
export function publishErrorMessage(status: number, json: unknown): string {
  if (status === 404) return REVISION_MESSAGES.not_found;
  if ([400, 413, 422].includes(status) && (json as { outcome?: unknown } | null)?.outcome === "rejected") return REVISION_MESSAGES.rejected;
  const code = (json as { error?: unknown } | null)?.error;
  return isMessageCode(code) ? REVISION_MESSAGES[code] : REVISION_MESSAGES.unavailable;
}

/** A created revision: `201` with the detail body. */
export const createdRevision = (status: number, json: unknown): RevisionDetail | null =>
  status === 201 && json && typeof json === "object" && "id" in json ? (json as RevisionDetail) : null;

/** `sha256:0123abcd…89ef`: enough to compare by eye; the copy button copies the whole digest. */
export const shortDigest = (digest: string) => (digest.length > 23 ? `${digest.slice(0, 15)}…${digest.slice(-4)}` : digest);

const enc = encodeURIComponent;

export const revisionsPath = (organizationId: string, publisherId: string, capabilityId: string) =>
  `/api/organizations/${enc(organizationId)}/publishers/${enc(publisherId)}/capabilities/${enc(capabilityId)}/revisions`;

export const DOWNLOAD_PARTS = ["native", "projection", "statement", "signature"] as const;

export type DownloadPart = (typeof DOWNLOAD_PARTS)[number];

export const downloadPath = (organizationId: string, publisherId: string, capabilityId: string, revisionId: string, part: DownloadPart) =>
  `${revisionsPath(organizationId, publisherId, capabilityId)}/${enc(revisionId)}/${part}`;

export const publicKeyPath = (organizationId: string, publisherId: string, keyUuid: string) =>
  `/api/organizations/${enc(organizationId)}/publishers/${enc(publisherId)}/signing-keys/${enc(keyUuid)}/public-key`;

/** Page links (not BFF routes). */
export const capabilityPage = (organizationId: string, publisherId: string, capabilityId: string) =>
  `/organizations/${enc(organizationId)}/publishers/${enc(publisherId)}/capabilities/${enc(capabilityId)}`;

export const revisionsPage = (organizationId: string, publisherId: string, capabilityId: string) =>
  `${capabilityPage(organizationId, publisherId, capabilityId)}/revisions`;

export const revisionPage = (organizationId: string, publisherId: string, capabilityId: string, revisionId: string) =>
  `${revisionsPage(organizationId, publisherId, capabilityId)}/${enc(revisionId)}`;

export const signingKeysPage = (organizationId: string, publisherId: string) => `/organizations/${enc(organizationId)}/publishers/${enc(publisherId)}/signing-keys`;
