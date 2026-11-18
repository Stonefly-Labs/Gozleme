/** Test data for the GOZ-0007 revision components (imported only by tests). */
import { proofWording, type RevisionDetail, type RevisionSummary, type SigningKey, type Verification } from "@/lib/revisions";

export const ORG = "0193a000-0000-7000-8000-000000000002";
export const PUB = "0193a000-0000-7000-8000-000000000010";
export const CAP = "0193a000-0000-7000-8000-000000000011";
export const REV = "0193a000-0000-7000-8000-000000000020";
export const KEY = "0193a000-0000-7000-8000-000000000030";
export const ORIGIN = "registry.demo.gozleme.test";
export const CANONICAL_CAP = `gz://${ORIGIN}/cap/${CAP}`;
export const CANONICAL_PUB = `gz://${ORIGIN}/pub/${PUB}`;
export const KEY_ID = `sha256:${"b".repeat(64)}`;
export const DIGEST = `sha256:${"a".repeat(64)}`;
export const ACCOUNT = "0193a000-0000-7000-8000-000000000040";

export const BANNED = ["delegat", "transfer", "chain of trust", "transparency", "publisher-held"];

export const verified: Verification = { status: "verified", reason: null, key: { id: KEY, keyId: KEY_ID, status: "active" } };

export const summary = (sequence: number, version: string, verification: Verification = verified): RevisionSummary => ({
  id: `0193a000-0000-7000-8000-0000000001${String(sequence).padStart(2, "0")}`,
  capabilityId: CANONICAL_CAP,
  version,
  sequence,
  publishedAt: "2026-11-18T10:15:30.123456+00:00",
  publishedBy: ACCOUNT,
  nativeDigest: DIGEST,
  verification,
});

export const detail = (overrides: Partial<RevisionDetail> = {}): RevisionDetail => ({
  ...summary(1, "1.0.0"),
  id: REV,
  publisherId: CANONICAL_PUB,
  publisherSuspended: false,
  envelope: { envelopeVersion: 1, family: "mcp", capability: CANONICAL_CAP, version: "1.0.0", native: { format: "mcp-server-manifest", formatVersion: "1", encoding: "json" } },
  native: { format: "mcp-server-manifest", formatVersion: "1", encoding: "json", document: '{"name":"io.example/weather"}\n', digest: DIGEST, byteLength: 30 },
  projection: {
    projectionVersion: "1",
    adapter: "mcp/1",
    family: "mcp",
    kind: "mcp-server",
    nativeName: "io.example/weather",
    title: "weather",
    description: "d",
    declaredVersion: "1.0.0",
    attributes: { mode: "stateless" },
    entries: [],
    derived: [],
  },
  projectionDigest: `sha256:${"c".repeat(64)}`,
  adapter: { id: "mcp", version: "1" },
  ruleCatalogueVersion: "1",
  schemaBundleVersion: "1",
  attestation: {
    statementVersion: 1,
    statement: {
      adapter: { id: "mcp", version: "1" },
      capability: CANONICAL_CAP,
      key: { algorithm: "Ed25519", id: KEY_ID },
      native: { byteLength: 30, digest: DIGEST, encoding: "json", format: "mcp-server-manifest", formatVersion: "1" },
      projection: { digest: `sha256:${"c".repeat(64)}`, version: "1" },
      publishedAt: "2026-11-18T10:15:30.123456Z",
      publisher: CANONICAL_PUB,
      registryOrigin: ORIGIN,
      sequence: 1,
      statementVersion: 1,
      type: "gozleme.revision-attestation",
      version: "1.0.0",
    },
    algorithm: "Ed25519",
    keyId: KEY_ID,
    proof: proofWording(ORIGIN, CANONICAL_PUB),
  },
  ...overrides,
});

export const key = (generation: number, overrides: Partial<SigningKey> = {}): SigningKey => ({
  id: `0193a000-0000-7000-8000-0000000003${String(generation).padStart(2, "0")}`,
  keyId: `sha256:${String(generation).repeat(64)}`,
  algorithm: "Ed25519",
  generation,
  createdAt: "2026-11-18T10:00:00+00:00",
  status: "active",
  ...overrides,
});
