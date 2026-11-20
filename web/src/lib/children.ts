/**
 * Child identity and projection generation shapes (docs/specs/008-child-identities/contracts/children-api.openapi.yaml),
 * the fixed notice (FR-014), kind labels and page links. Mirrors `src/Gozleme.Api/Children/ChildContracts.cs`.
 * Server-side reads live in `children-server.ts`.
 */
import { capabilityPage, revisionsPath } from "@/lib/revisions";

export type ChildKind = "tool" | "resource" | "resource-template" | "prompt" | "skill" | "operation" | "webhook" | "channel";

export const KIND_LABELS: Record<ChildKind, string> = {
  tool: "Tool",
  resource: "Resource",
  "resource-template": "Resource template",
  prompt: "Prompt",
  skill: "Skill",
  operation: "Operation",
  webhook: "Webhook",
  channel: "Channel",
};

export const kindLabel = (kind: string) => (Object.hasOwn(KIND_LABELS, kind) ? KIND_LABELS[kind as ChildKind] : kind);

/** Shown on every child view (FR-014, GOZ-0006-IF-12). */
export const DECLARED_NOT_VERIFIED = "Children are declared by the publisher in the manifest. They are not verified behaviour.";

export const NO_CHILDREN = "This capability declares no children.";

export const NO_REVISION_CHILDREN = "This revision declares no children.";

/** FR-016a: a later entry with the same kind and native name in a revision published before this feature. */
export const DUPLICATE_ENTRY =
  "Duplicate entry: this entry repeats the kind and native name of an earlier entry in a revision published before child identities. It is never matched by later revisions.";

export const ATTESTED = "Attested at publication";

export const NOT_ATTESTED = "Not attested";

export const GENERATIONS_NOTE =
  "Generation 1 is the projection stored and signed at publication. A registry operator can re-project a revision; later generations are recorded beside it and are not covered by the attestation.";

export const COMPARISON_LABELS: Record<GenerationComparison, string> = {
  identical: "Same digest as the previous generation",
  different: "Different digest from the previous generation",
  not_comparable: "Not comparable with the previous generation (different adapter or projection version)",
};

export type GenerationComparison = "identical" | "different" | "not_comparable";

export interface ChildLatest {
  revisionId: string;
  sequence: number;
  version: string;
  nativeName: string;
  title: string | null;
}

export interface ChildSummary {
  id: string;
  /** `gz://<origin>/cap/<capability>/<kind>/<child>`. */
  canonicalId: string;
  kind: string;
  ordinal: number;
  duplicate: boolean;
  firstRevisionId: string;
  latest: ChildLatest;
  presentIn: number;
  createdAt: string;
}

export interface ChildList {
  items: ChildSummary[];
  next: number | null;
  declaredNotVerified: true;
}

export interface ChildHistoryEntry {
  revisionId: string;
  sequence: number;
  version: string;
  present: boolean;
  nativeName: string | null;
  title: string | null;
}

export interface ChildDetail {
  child: ChildSummary;
  capability: { id: string; canonicalId: string; displayName: string };
  history: ChildHistoryEntry[];
  next: number | null;
  declaredNotVerified: true;
}

export interface RevisionChild {
  entryIndex: number;
  childId: string;
  canonicalId: string;
  kind: string;
  nativeName: string;
  title: string | null;
  duplicate: boolean;
}

export interface RevisionChildList {
  items: RevisionChild[];
  next: number | null;
  declaredNotVerified: true;
}

export interface ProjectionGeneration {
  id: string;
  generation: number;
  adapter: { id: string; version: string };
  projectionVersion: string;
  digest: string;
  byteLength: number;
  attested: boolean;
  createdAt: string;
  comparedToPrevious?: GenerationComparison;
}

export interface GenerationList {
  items: ProjectionGeneration[];
}

const enc = encodeURIComponent;

/** Page links (not BFF routes). */
export const childrenPage = (organizationId: string, publisherId: string, capabilityId: string) => `${capabilityPage(organizationId, publisherId, capabilityId)}/children`;

export const childPage = (organizationId: string, publisherId: string, capabilityId: string, childId: string) =>
  `${childrenPage(organizationId, publisherId, capabilityId)}/${enc(childId)}`;

/** The BFF download of one generation's canonical projection bytes. */
export const generationDownloadPath = (organizationId: string, publisherId: string, capabilityId: string, revisionId: string, generationId: string) =>
  `${revisionsPath(organizationId, publisherId, capabilityId)}/${enc(revisionId)}/projections/${enc(generationId)}`;
