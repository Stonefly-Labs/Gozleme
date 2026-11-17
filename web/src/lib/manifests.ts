/**
 * Manifest validation v1 shapes (docs/specs/006-typed-manifests/contracts/manifest-validation.md), the bundled
 * samples (research R15) and client-safe messages. Mirrors `src/Gozleme.Api/Manifests/ManifestContracts.cs`.
 */
import { sampleFiles, sampleIndex } from "@/samples/manifests/samples.generated";

export type Family = "mcp" | "a2a" | "api" | "event" | "skill" | "model";

export const FAMILIES: { id: Family; label: string; format: string }[] = [
  { id: "mcp", label: "MCP server", format: "mcp-server-manifest 1" },
  { id: "a2a", label: "A2A agent", format: "a2a-agent-card 1.0" },
  { id: "api", label: "API (OpenAPI)", format: "openapi 3.1 / 3.0" },
  { id: "event", label: "Events (AsyncAPI)", format: "asyncapi 3.0.0" },
  { id: "skill", label: "Agent Skill", format: "agent-skills 2026-11-17" },
  { id: "model", label: "Model card", format: "gozleme-model-card 1" },
];

export type ProblemLocation = { part: "envelope" | "native"; pointer: string } | { part: "envelope" | "native"; line: number; column: number } | null;

export interface Problem {
  category: string;
  rule: string;
  location: ProblemLocation;
  message: string;
}

export interface EnvelopeSummary {
  envelopeVersion: number;
  family: Family;
  capability: string;
  version: string;
  title?: string;
  summary?: string;
  native: { format: string; formatVersion: string; encoding: string };
}

export interface NativeResult {
  format: string;
  formatVersion: string;
  encoding: string;
  document: string;
  digest: string;
  byteLength: number;
}

export interface ProjectionEntry {
  kind: string;
  nativeName: string;
  title?: string;
}

export interface Projection {
  projectionVersion: string;
  adapter: string;
  family: Family;
  kind: string;
  nativeName: string;
  title: string;
  description: string;
  declaredVersion: string;
  attributes: Record<string, unknown>;
  entries: ProjectionEntry[];
  derived: string[];
}

export type ValidationResult =
  | { outcome: "accepted"; envelope: EnvelopeSummary; native: NativeResult; projection: Projection }
  | { outcome: "rejected"; problems: Problem[]; truncated: boolean };

export interface Sample {
  id: string;
  family: Family;
  title: string;
  file: string;
  valid: boolean;
}

export const PLACEHOLDER = "{{capability}}";

export const samples: Sample[] = sampleIndex.samples.map((s) => ({
  id: s.id,
  family: s.family as Family,
  title: s.title,
  file: s.file,
  valid: s.expected.outcome === "accepted",
}));

export const samplesFor = (family: Family) => samples.filter((s) => s.family === family);

/** The sample envelope as pretty JSON with `{{capability}}` replaced by the target's canonical ID. */
export function sampleText(sample: Sample, canonicalId: string): string {
  return JSON.stringify(sampleFiles[sample.file], null, 2).replaceAll(PLACEHOLDER, canonicalId);
}

/** How to read a problem location: an RFC 6901 pointer, a line and column, or none. */
export function locationText(location: ProblemLocation): string {
  if (location === null) return "—";
  if ("pointer" in location) return `${location.part} ${location.pointer === "" ? "(root)" : location.pointer}`;
  return `${location.part} line ${location.line}, column ${location.column}`;
}

export const MANIFEST_MESSAGES = {
  forbidden: "You don't have permission to validate manifests for this capability. The publisher role is required.",
  suspended: "This publisher is suspended, so its manifests can't be validated.",
  rate_limited: "Too many validations. Wait a minute and try again.",
  unavailable: "The service is unavailable. Try again shortly.",
  not_found: "Capability not found.",
  too_large: "The request is larger than 1 MiB.",
  unsupported_media_type: "The request must be sent as application/json.",
} as const;

const isMessageCode = (code: unknown): code is keyof typeof MANIFEST_MESSAGES => typeof code === "string" && Object.hasOwn(MANIFEST_MESSAGES, code);

/** The message for a non-200 answer, from its status and error body. */
export function errorMessage(status: number, json: unknown): string {
  if (status === 404) return MANIFEST_MESSAGES.not_found;
  const code = (json as { error?: unknown } | null)?.error;
  return isMessageCode(code) ? MANIFEST_MESSAGES[code] : MANIFEST_MESSAGES.unavailable;
}

export const validatePath = (organizationId: string, publisherId: string, capabilityId: string) =>
  `/api/organizations/${encodeURIComponent(organizationId)}/publishers/${encodeURIComponent(publisherId)}/capabilities/${encodeURIComponent(capabilityId)}/manifests/validate`;

/** Posts the envelope text exactly as typed; network failures read as 503. */
export async function postManifest(path: string, text: string): Promise<{ status: number; json: unknown }> {
  try {
    const res = await fetch(path, { method: "POST", headers: { "content-type": "application/json" }, body: text });
    const body = await res.text();
    let json: unknown = null;
    try {
      json = body ? JSON.parse(body) : null;
    } catch {
      json = null;
    }
    return { status: res.status, json };
  } catch {
    return { status: 503, json: null };
  }
}
