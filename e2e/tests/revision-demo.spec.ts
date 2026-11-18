import AxeBuilder from "@axe-core/playwright";
import { createHash, createPublicKey, verify } from "node:crypto";
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { expect, test, type Browser, type Page } from "@playwright/test";
import { API_URL } from "../playwright.config";
import { repoRoot, secretStrings } from "./compose";
import { attempt, execute, tamper } from "./db";
import { DEMO, DEMO_ORG, DEMO_PASSWORD, PARTNER_ORG, evidenceDir, shotsDir } from "./evidence";
import { operator, reclaim, succession } from "./operator";
import { JAEGER_URL, METRICS_URL, recentSpans, storedLogs } from "./telemetry";

/**
 * GOZ-0007 autonomous demo (quickstart steps 1–8). carol publishes two revisions of the seeded capability in the
 * browser and sees them verified; republishing, SQL mutation as both logins and HTTP mutation are refused, and a
 * superuser byte change shows **Failed** with its reason; every family's downloads verify with Node's own Ed25519,
 * SHA-256 and canonical JSON; a rename and a reclaim leave revisions verified under a **Suspended publisher** badge;
 * operator key succession (`lost`, `compromised`) retires a key that still verifies; no delegation route or wording
 * exists; roles match the contract and the 11th publication in a minute is 429; Jaeger shows the publish, verify and
 * operator spans, and no key material or content reaches telemetry or logs.
 */
const shots = shotsDir("goz-0007");
const evidence = evidenceDir("goz-0007");
const suiteStarted = Date.now();
const ORIGIN = process.env.GOZLEME_REGISTRY_ORIGIN ?? "registry.demo.gozleme.test";
const KEK = process.env.GOZLEME_SIGNING_KEK ?? "bG9jYWwtZGV2LXNpZ25pbmcta2VrLW5vdC1zZWNyZXQ=";
const PUBLISHER = { id: "0193a000-0000-7000-8000-000000000010", handle: "seeded-demo" } as const;
const CAPABILITY = { id: "0193a000-0000-7000-8000-000000000011", name: "manifest-demo" } as const;
const CANONICAL = `gz://${ORIGIN}/cap/${CAPABILITY.id}`;
const CANONICAL_PUB = `gz://${ORIGIN}/pub/${PUBLISHER.id}`;
const ALIAS = `${PUBLISHER.handle}/${CAPABILITY.name}`;
const orgApi = (org: string) => `${API_URL}/api/v1/organizations/${org}`;
const ORG_API = orgApi(DEMO_ORG.id);
const capApi = (publisher: string, capability: string) => `${ORG_API}/publishers/${publisher}/capabilities/${capability}`;
const REVISIONS = `${capApi(PUBLISHER.id, CAPABILITY.id)}/revisions`;
const VALIDATE = `${capApi(PUBLISHER.id, CAPABILITY.id)}/manifests/validate`;
const keysApi = (publisher: string) => `${ORG_API}/publishers/${publisher}/signing-keys`;
const ROLES = ["admin", "approver", "member", "publisher", "reviewer"] as const;
const BANNED = ["delegat", "transfer", "chain of trust", "transparency", "publisher-held"];
const TABLES = ["manifest_revisions", "revision_attestations", "publisher_signing_keys", "signing_key_retirements"];
const UUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/;
// Revisions are never deleted, so every run publishes run-unique pre-release versions (A7) and run-unique publishers.
const run = String(Math.floor(100000 + Math.random() * 900000));
const V1 = `1.0.0-r${run}`;
const V2 = `1.1.0-r${run}`;
const PROVENANCE = `revdemo-${run}`;
const KEYS = `revkey-${run}`;

const SAMPLES = `${repoRoot}web/src/samples/manifests`;
type Sample = { id: string; family: string; file: string; expected: { outcome: string } };
const index = JSON.parse(readFileSync(`${SAMPLES}/index.json`, "utf8")) as { samples: Sample[] };
const firstPerFamily = [...new Map(index.samples.filter((s) => s.expected.outcome === "accepted").map((s) => [s.family, s] as const)).values()].reverse();
const sampleById = (id: string) => index.samples.find((s) => s.id === id)!;
/** A sample's envelope for `capability` with `version` as the declared version. */
const envelope = (sample: Sample, version: string, capability = CANONICAL) => {
  const json = JSON.parse(readFileSync(`${SAMPLES}/${sample.file}`, "utf8").replaceAll("{{capability}}", capability)) as Record<string, unknown>;
  json.version = version;
  return JSON.stringify(json, null, 2);
};
const OPENAPI = sampleById("valid-openapi-3.1");

let alice: Page;
let carol: Page;
let dave: Page;
let aliceId: string;
let carolId: string;
let daveId: string;
const record: Record<string, unknown> = {};
const visited: string[] = []; // page text of every revision page, for the wording scan (step 6)
const secrets = { statements: [] as Buffer[], signatures: [] as Buffer[], digests: [] as string[], versions: [V1, V2] as string[], names: [CAPABILITY.name] as string[] };
let first: { id: string; sequence: number };
let second: { id: string; sequence: number };

test.describe.configure({ mode: "serial" });

async function expectAccessible(page: Page) {
  await expect(page).toHaveTitle(/ — Gözleme$|^Gözleme — /);
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  expect(violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
}

async function signedInPage(browser: Browser, email: string) {
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, colorScheme: "light" });
  const page = await context.newPage();
  await page.goto("/login");
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill(DEMO_PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByTestId("account-email")).toHaveText(email);
  return page;
}

/** A direct API call with the web-client header (CSRF layer 2); the body is sent exactly as given. */
async function api(page: Page, method: string, url: string, body?: string) {
  const res = await page.request.fetch(url, { method, headers: { "x-gozleme-client": "web", "content-type": "application/json" }, data: body });
  const bytes = await res.body();
  const text = bytes.toString("utf8");
  return { status: res.status(), text, bytes, headers: res.headers(), json: () => JSON.parse(text) as Record<string, any> };
}

/** Publishes through the API; a 429 from the shared per-minute budget waits out the window (step 7 tests the limit). */
async function publish(page: Page, body: string, url = REVISIONS) {
  for (;;) {
    const r = await api(page, "POST", url, body);
    if (r.status !== 429) return r;
    await page.waitForTimeout((Number(r.headers["retry-after"]) + 1) * 1000);
  }
}

async function published(page: Page, body: string, url = REVISIONS) {
  const r = await publish(page, body, url);
  expect(r.status, r.text).toBe(201);
  return r.json();
}

const setRoles = (page: Page, org: string, userId: string, roles: string[]) => api(page, "PUT", `${orgApi(org)}/members/${userId}/roles`, JSON.stringify({ roles }));
const userId = async (page: Page) => String((await (await page.request.get(`${API_URL}/api/v1/auth/session`)).json()).userId);
const sha256 = (bytes: Buffer) => `sha256:${createHash("sha256").update(bytes).digest("hex")}`;

/** RFC 8785 for the v1 subset (integers, strings, objects, arrays): JS sorts keys by UTF-16 code units, as JCS does. */
function canonical(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(canonical).join(",")}]`;
  if (value !== null && typeof value === "object") {
    const obj = value as Record<string, unknown>;
    return `{${Object.keys(obj).sort().map((k) => `${JSON.stringify(k)}:${canonical(obj[k])}`).join(",")}}`;
  }
  return JSON.stringify(value);
}

const pagePath = (publisher: string, capability: string) => `/organizations/${DEMO_ORG.id}/publishers/${publisher}/capabilities/${capability}`;

async function shot(page: Page, file: string) {
  visited.push((await page.locator("body").textContent()) ?? "");
  await page.screenshot({ path: `${shots}/${file}`, fullPage: true });
}

test.afterAll(async () => {
  // Restore the seeded roles even if a step failed midway: alice {admin, member}, carol {member, publisher}.
  if (alice && aliceId) await setRoles(alice, DEMO_ORG.id, aliceId, ["admin"]);
  if (alice && carolId) await setRoles(alice, DEMO_ORG.id, carolId, ["publisher"]);
  for (const page of [alice, carol, dave]) await page?.context().close();
});

test("1. a publisher member publishes two revisions in the browser and both verify", async ({ browser }) => {
  test.setTimeout(240_000);
  alice = await signedInPage(browser, DEMO.alice);
  carol = await signedInPage(browser, DEMO.carol);
  dave = await signedInPage(browser, DEMO.dave);
  [aliceId, carolId, daveId] = [await userId(alice), await userId(carol), await userId(dave)];
  expect((await setRoles(alice, DEMO_ORG.id, carolId, ["publisher"])).status).toBe(200);

  const started = Date.now();
  const ids: string[] = [];
  for (const version of [V1, V2]) {
    await carol.goto(`/organizations/${DEMO_ORG.id}/publishers/${PUBLISHER.id}`);
    const row = carol.getByTestId("capability-row").filter({ hasText: ALIAS });
    await row.getByTestId("validate-manifest-link").click();
    await expect(carol.getByTestId("capability-canonical-id")).toHaveText(CANONICAL);
    await carol.getByTestId("manifest-text").fill(envelope(OPENAPI, version));
    await carol.getByTestId("validate").click();
    await expect(carol.getByTestId("manifest-accepted")).toBeVisible();
    await expect(carol.getByTestId("publish")).toBeVisible();
    if (version === V1) await shot(carol, "01-publish-offered.png");
    await carol.getByTestId("publish").click();
    await carol.waitForURL(new RegExp(`${pagePath(PUBLISHER.id, CAPABILITY.id)}/revisions/${UUID.source}$`));
    await expect(carol.getByTestId("revision-version")).toHaveText(version);
    await expect(carol.getByTestId("verification-badge")).toHaveAttribute("data-status", "verified");
    ids.push(carol.url().split("/").pop()!);
  }

  const elapsed = (Date.now() - started) / 1000;
  const detail1 = (await api(carol, "GET", `${REVISIONS}/${ids[0]}`)).json();
  const detail2 = (await api(carol, "GET", `${REVISIONS}/${ids[1]}`)).json();
  first = { id: ids[0]!, sequence: detail1.sequence };
  second = { id: ids[1]!, sequence: detail2.sequence };
  expect(second.sequence).toBe(first.sequence + 1);
  secrets.digests.push(detail1.nativeDigest, detail2.nativeDigest);

  // The list (from this run's first revision): sequence, version, account, digest and a Verified badge each.
  await carol.goto(`${pagePath(PUBLISHER.id, CAPABILITY.id)}/revisions?after=${first.sequence - 1}`);
  const rows = carol.getByTestId("revision-row");
  await expect(rows.first().getByTestId("revision-version")).toHaveText(V1);
  await expect(rows.nth(1).getByTestId("revision-version")).toHaveText(V2);
  for (const i of [0, 1]) {
    await expect(rows.nth(i).getByTestId("verification-badge")).toHaveAttribute("data-status", "verified");
    await expect(rows.nth(i).getByTestId("revision-published-by")).toHaveText(carolId);
  }
  await expect(carol.getByText(/latest/i)).toHaveCount(0);
  await expectAccessible(carol);
  await shot(carol, "02-revision-list.png");

  await carol.goto(`${pagePath(PUBLISHER.id, CAPABILITY.id)}/revisions/${first.id}`);
  await expect(carol.getByTestId("native-document")).toBeVisible();
  await expectAccessible(carol);
  await shot(carol, "03-revision-native.png");
  await carol.getByTestId("tab-projection").click();
  await expect(carol.getByTestId("projection-version")).toContainText("projection v1");
  await shot(carol, "04-revision-projection.png");
  await carol.getByTestId("tab-attestation").click();
  await expect(carol.getByTestId("attestation-proof")).toHaveText(
    `Signed by registry ${ORIGIN} for publisher ${CANONICAL_PUB} with a registry-held key. This shows that this registry accepted the revision from an authorized publisher member. It is not a signature made by the publisher.`,
  );
  await expect(carol.getByTestId("attestation-algorithm")).toHaveText("Ed25519");
  await expectAccessible(carol);
  await shot(carol, "05-revision-attestation.png");

  await carol.goto(`/organizations/${DEMO_ORG.id}/publishers/${PUBLISHER.id}/signing-keys`);
  await expect(carol.locator('[data-testid="signing-key-row"][data-key-status="active"]')).toHaveCount(1);
  await expectAccessible(carol);
  await shot(carol, "06-signing-keys.png");

  Object.assign(record, { publish: { revisions: ids, sequences: [first.sequence, second.sequence], elapsedSeconds: Math.round(elapsed), keyId: detail1.attestation.keyId } });
});

test("2. republication, SQL and HTTP mutation are refused, and a changed byte fails verification", async () => {
  test.setTimeout(240_000);
  // Same version with different bytes, identical bytes and a +build variant: one answer.
  const refusals: Record<string, string> = {};
  for (const [label, body] of [
    ["different", envelope(sampleById("valid-openapi-3.0"), V1)],
    ["identical", envelope(OPENAPI, V1)],
    ["build", envelope(OPENAPI, `${V1}+build.7`)],
  ] as const) {
    const r = await publish(carol, body);
    expect([r.status, r.text]).toEqual([409, '{"error":"version_exists"}']);
    refusals[label] = `${r.status} ${r.text}`;
  }
  for (const method of ["PUT", "PATCH", "DELETE"]) {
    expect((await api(carol, method, `${REVISIONS}/${first.id}`, "{}")).status, method).toBe(405);
    refusals[method] = "405";
  }

  // Through psql as both logins, with carol's context, every write to every table changes nothing.
  const sql: Record<string, string> = {};
  const where = (t: string) => (t.startsWith("manifest") ? `id = '${first.id}'` : t === "revision_attestations" ? `revision_id = '${first.id}'` : `organization_id = '${DEMO_ORG.id}'`);
  for (const role of ["gozleme_app", "gozleme_migrator"]) {
    for (const table of TABLES) {
      const column = table === "manifest_revisions" ? "version = version" : table === "revision_attestations" ? "signature = signature" : table === "publisher_signing_keys" ? "generation = generation" : "reason = reason";
      for (const [op, statement] of [
        ["update", `UPDATE gozleme.${table} SET ${column} WHERE ${where(table)}`],
        ["delete", `DELETE FROM gozleme.${table} WHERE ${where(table)}`],
      ] as const) {
        const outcome = attempt(role, carolId, statement);
        expect(["0 row(s)", "42501", "P0001"], `${role} ${op} ${table}`).toContain(outcome);
        sql[`${role} ${op} ${table}`] = outcome;
      }
      const truncate = execute(role, carolId, `TRUNCATE gozleme.${table} CASCADE`);
      expect(truncate, `${role} truncate ${table}`).not.toBe("ok");
      sql[`${role} truncate ${table}`] = truncate;
    }
  }
  for (const id of [first.id, second.id]) expect((await api(carol, "GET", `${REVISIONS}/${id}`)).json().verification.status).toBe("verified");

  // Tamper: one byte of a dedicated run-unique revision per part, as the superuser with triggers disabled.
  const tampered: Record<string, { revision: string; reason: string }> = {};
  const cases = [
    ["native", "native_digest_mismatch", "manifest_revisions", "native_document", "id"],
    ["projection", "projection_digest_mismatch", "manifest_revisions", "projection", "id"],
    ["statement", "signature_invalid", "revision_attestations", "statement", "revision_id"],
    ["signature", "signature_invalid", "revision_attestations", "signature", "revision_id"],
  ] as const;
  for (const [part, reason, table, column, key] of cases) {
    const revision = await published(carol, envelope(OPENAPI, `0.0.1-tamper.${part}.r${run}`));
    // Flip the low bit of a byte that keeps every part parseable. In the statement it is the first hex digit of
    // native.digest: the bytes stay canonical and key.id is untouched, so only the signature check can fail.
    const at = part === "statement" ? `position('"digest":"sha256:'::bytea IN ${column}) + 17` : part === "signature" ? "1" : "3";
    expect(tamper(`UPDATE gozleme.${table} SET ${column} = set_byte(${column}, (${at})::int - 1, get_byte(${column}, (${at})::int - 1) # 1) WHERE ${key} = '${revision.id}'`)).toBe("1");
    const read = (await api(carol, "GET", `${REVISIONS}/${revision.id}`)).json();
    expect([read.verification.status, read.verification.reason], part).toEqual(["failed", reason]);
    tampered[part] = { revision: revision.id, reason };
  }
  await carol.goto(`${pagePath(PUBLISHER.id, CAPABILITY.id)}/revisions/${tampered.native!.revision}`);
  await expect(carol.getByTestId("verification-badge")).toHaveAttribute("data-status", "failed");
  await expect(carol.getByTestId("verification-reason")).toBeVisible();
  await shot(carol, "10-tampered-failed.png");

  Object.assign(record, { immutability: { republish: refusals, sql }, tamper: { runId: run, revisions: tampered } });
});

test("3. every family's downloads verify independently with Node", async () => {
  test.setTimeout(240_000);
  const families: Record<string, { revision: string; keyId: string }> = {};
  for (const [i, sample] of firstPerFamily.entries()) {
    const revision = await published(carol, envelope(sample, `2.${i}.0-r${run}`));
    const base = `${REVISIONS}/${revision.id}`;
    const downloads: Record<string, Buffer> = {};
    for (const part of ["native", "projection", "statement", "signature"]) {
      const r = await api(carol, "GET", `${base}/${part}`);
      expect(r.status).toBe(200);
      expect(r.headers["content-disposition"]).toMatch(/^attachment;/);
      expect(r.headers["x-content-type-options"]).toBe("nosniff");
      expect(r.headers["cache-control"]).toContain("no-store");
      downloads[part] = r.bytes;
    }
    const keys = (await api(carol, "GET", keysApi(PUBLISHER.id))).json().keys as { id: string; keyId: string }[];
    const statement = JSON.parse(downloads.statement!.toString("utf8"));
    const key = keys.find((k) => k.keyId === statement.key.id)!;
    const pem = (await api(carol, "GET", `${keysApi(PUBLISHER.id)}/${key.id}/public-key`)).bytes;

    expect(verify(null, downloads.statement!, createPublicKey(pem), downloads.signature!), sample.id).toBe(true);
    const raw = createPublicKey(pem).export({ format: "der", type: "spki" }).subarray(-32);
    expect(sha256(raw)).toBe(statement.key.id);
    expect(sha256(downloads.native!)).toBe(statement.native.digest);
    expect(downloads.native!.length).toBe(statement.native.byteLength);
    expect(sha256(downloads.projection!)).toBe(statement.projection.digest);
    expect(canonical(JSON.parse(downloads.projection!.toString("utf8")))).toBe(downloads.projection!.toString("utf8"));
    expect(canonical(statement)).toBe(downloads.statement!.toString("utf8"));
    expect(statement.capability).toBe(CANONICAL);
    secrets.statements.push(downloads.statement!);
    secrets.signatures.push(downloads.signature!);
    secrets.digests.push(statement.native.digest);
    secrets.versions.push(`2.${i}.0-r${run}`);
    families[sample.family] = { revision: revision.id, keyId: statement.key.id };
  }
  expect(Object.keys(families).sort()).toEqual(["a2a", "api", "event", "mcp", "model", "skill"]);
  Object.assign(record, { independentVerification: { verifier: "node:crypto Ed25519 + sha256 + RFC 8785 re-canonicalization", families } });
});

test("4. a rename and a reclaim leave earlier revisions verified under a suspended publisher", async () => {
  test.setTimeout(240_000);
  const registered = await api(alice, "POST", `${ORG_API}/publishers`, JSON.stringify({ handle: PROVENANCE, displayName: "Revision Demo Tools" }));
  expect(registered.status).toBe(201);
  const publisherId = registered.json().id as string;
  const capability = (await api(carol, "POST", `${ORG_API}/publishers/${publisherId}/capabilities`, JSON.stringify({ name: "revision-forecast", displayName: "Revision Forecast" }))).json();
  secrets.names.push("revision-forecast");
  const url = `${capApi(publisherId, capability.id)}/revisions`;
  const revision = await published(carol, envelope(OPENAPI, `1.0.0-r${run}`, capability.canonicalId), url);
  const before = (await api(carol, "GET", `${url}/${revision.id}`)).json();

  expect((await api(carol, "PATCH", capApi(publisherId, capability.id), JSON.stringify({ displayName: "Revision Forecast v2" }))).status).toBe(200);
  const renamed = (await api(carol, "GET", `${url}/${revision.id}`)).json();
  expect(JSON.stringify(renamed)).toBe(JSON.stringify(before));

  const reclaimed = reclaim(PROVENANCE);
  expect(reclaimed.exit, reclaimed.stderr).toBe(0);
  const after = (await api(carol, "GET", `${url}/${revision.id}`)).json();
  expect([after.publisherSuspended, after.verification.status]).toEqual([true, "verified"]);
  for (const member of ["capabilityId", "publisherId", "nativeDigest", "projectionDigest", "attestation", "publishedAt", "sequence"]) expect(after[member]).toEqual(before[member]);
  const statement = JSON.stringify(after.attestation.statement);
  for (const name of [PROVENANCE, "Revision Forecast", "revision-forecast"]) expect(statement).not.toContain(name);
  expect(await publish(carol, envelope(OPENAPI, `2.0.0-r${run}`, capability.canonicalId), url).then((r) => [r.status, r.text])).toEqual([409, '{"error":"suspended"}']);

  await carol.goto(`${pagePath(publisherId, capability.id)}/revisions`);
  await expect(carol.getByTestId("suspended-publisher-banner")).toBeVisible();
  await expectAccessible(carol);
  await shot(carol, "20-suspended-publisher-list.png");
  await carol.goto(`${pagePath(publisherId, capability.id)}/revisions/${revision.id}`);
  await expect(carol.getByTestId("suspended-publisher-banner")).toBeVisible();
  await expect(carol.getByTestId("verification-badge")).toHaveAttribute("data-status", "verified");
  await shot(carol, "21-suspended-publisher-detail.png");
  Object.assign(record, { provenance: { publisher: publisherId, capability: capability.id, revision: revision.id, reclaimExit: reclaimed.exit, suspendedPublish: 409 } });
});

test("5. operator key succession retires a key that still verifies what it signed", async () => {
  test.setTimeout(240_000);
  const publisherId = (await api(alice, "POST", `${ORG_API}/publishers`, JSON.stringify({ handle: KEYS, displayName: "Key Demo Tools" }))).json().id as string;
  const capability = (await api(carol, "POST", `${ORG_API}/publishers/${publisherId}/capabilities`, JSON.stringify({ name: "key-forecast", displayName: "Key Forecast" }))).json();
  secrets.names.push("key-forecast");
  const url = `${capApi(publisherId, capability.id)}/revisions`;
  const keysPage = `/organizations/${DEMO_ORG.id}/publishers/${publisherId}/signing-keys`;
  await carol.goto(keysPage);
  await expect(carol.getByTestId("no-key-yet")).toBeVisible();
  const rev1 = await published(carol, envelope(OPENAPI, `1.0.0-r${run}`, capability.canonicalId), url);

  const lost = succession(publisherId, "lost");
  expect(lost.exit, lost.stderr).toBe(0);
  expect(lost.stdout).toMatch(new RegExp(`^${publisherId} retired ${UUID.source} successor ${UUID.source}$`, "m"));
  for (const text of [lost.stdout, lost.stderr]) expect(text).not.toContain(KEK);
  let keys = (await api(carol, "GET", keysApi(publisherId))).json().keys as { generation: number; status: string; retiredReason?: string; keyId: string }[];
  expect(keys.map((k) => [k.generation, k.status, k.retiredReason ?? null])).toEqual([[1, "retired", "lost"], [2, "active", null]]);
  const reread = (await api(carol, "GET", `${url}/${rev1.id}`)).json();
  expect([reread.verification.status, reread.verification.key.status, reread.verification.key.retiredReason]).toEqual(["verified", "retired", "lost"]);
  const rev2 = await published(carol, envelope(OPENAPI, `1.1.0-r${run}`, capability.canonicalId), url);
  expect(rev2.attestation.keyId).toBe(keys[1]!.keyId);
  await carol.goto(keysPage);
  await expect(carol.locator('[data-testid="signing-key-row"][data-key-status="retired"]')).toHaveCount(1);
  await expectAccessible(carol);
  await shot(carol, "30-succession-lost-keys.png");
  await carol.goto(`${pagePath(publisherId, capability.id)}/revisions/${rev1.id}`);
  await carol.getByTestId("tab-attestation").click();
  await expect(carol.getByTestId("key-retired-reason")).toHaveText("lost");
  await shot(carol, "31-succession-lost-revision.png");

  const compromised = succession(publisherId, "compromised");
  expect(compromised.exit, compromised.stderr).toBe(0);
  keys = (await api(carol, "GET", keysApi(publisherId))).json().keys;
  expect(keys.map((k) => k.status)).toEqual(["retired", "retired", "active"]);
  await carol.goto(keysPage);
  await expect(carol.getByTestId("compromised-banner")).toBeVisible();
  await shot(carol, "32-succession-compromised-keys.png");
  await carol.goto(`${pagePath(publisherId, capability.id)}/revisions/${rev2.id}`);
  await carol.getByTestId("tab-attestation").click();
  await expect(carol.getByTestId("compromised-banner")).toBeVisible();
  await expect(carol.getByTestId("verification-badge")).toHaveAttribute("data-status", "verified");
  await shot(carol, "33-succession-compromised-revision.png");

  const usage = operator(["signing-key-succession", "--publisher", publisherId]);
  const unknown = operator(["signing-key-succession", "--publisher", "0193a000-0000-7000-8000-00000000dead", "--reason", "lost"]);
  expect([usage.exit, unknown.exit]).toEqual([64, 2]);
  Object.assign(record, { succession: { runId: run, publisher: publisherId, lost: lost.stdout, compromised: compromised.stdout, exits: { ok: 0, usage: usage.exit, unknown: unknown.exit } } });
});

test("6. no delegation route or wording exists, and a manifest cannot claim a signature", async () => {
  const boundary: Record<string, number> = {};
  for (const path of ["rotate", "replace", "transfer", "delegate"].map((p) => `${keysApi(PUBLISHER.id)}/${p}`).concat(`${API_URL}/api/v1/publish`)) {
    for (const method of ["GET", "POST", "PUT", "PATCH", "DELETE"]) {
      const { status } = await api(carol, method, path, method === "GET" ? undefined : "{}");
      expect(status, `${method} ${path}`).toBe(404);
      boundary[`${method} ${path.replace(API_URL, "")}`] = status;
    }
  }
  const responses = await Promise.all([REVISIONS, `${REVISIONS}/${first.id}`, `${REVISIONS}/${first.id}/statement`, keysApi(PUBLISHER.id)].map(async (u) => (await api(carol, "GET", u)).text));
  for (const text of [...responses, ...visited]) for (const banned of BANNED) expect(text.toLowerCase()).not.toContain(banned);

  const claim = readFileSync(`${SAMPLES}/${sampleById("invalid-a2a-signatures").file}`, "utf8").replaceAll("{{capability}}", CANONICAL);
  const refused = await publish(carol, claim);
  const validated = await api(carol, "POST", VALIDATE, claim);
  expect(refused.status).toBe(422);
  expect(refused.text).toContain('"reserved_claim"');
  expect(refused.text).toBe(validated.text);
  Object.assign(record, { boundary, reservedClaim: refused.status, wordingScan: { pages: visited.length, responses: responses.length, banned: BANNED } });
});

test("7. roles match the contract, non-members get the empty 404 and the 11th publication is rate limited", async () => {
  test.setTimeout(240_000);
  // alice (admin): no Publish revision control (she cannot validate either), and a direct POST is 403.
  await alice.goto(`${pagePath(PUBLISHER.id, CAPABILITY.id)}/manifest`);
  await expect(alice.getByTestId("publish")).toHaveCount(0);
  expect((await api(alice, "POST", REVISIONS, "{not json")).status).toBe(403);

  const reads = [REVISIONS, `${REVISIONS}/${first.id}`, `${REVISIONS}/${first.id}/native`, `${REVISIONS}/${first.id}/signature`, keysApi(PUBLISHER.id)];
  const matrix: Record<string, Record<string, number>> = {};
  for (const role of ROLES) {
    expect((await setRoles(alice, DEMO_ORG.id, carolId, [role])).status).toBe(200);
    const row: Record<string, number> = {};
    for (const url of reads) row[`GET ${url.replace(ORG_API, "")}`] = (await api(carol, "GET", url)).status;
    // A malformed body: 400 proves the request passed authorization; it creates nothing.
    row.publish = (await publish(carol, "{not json")).status;
    expect(Object.values(row).slice(0, reads.length).every((s) => s === 200), role).toBe(true);
    expect(row.publish, role).toBe(role === "publisher" ? 400 : 403);
    matrix[role] = row;
  }
  expect((await setRoles(alice, DEMO_ORG.id, carolId, ["publisher"])).status).toBe(200);

  // dave (Seeded Partner Org only): the identical empty 404 everywhere.
  for (const url of [...reads, `${keysApi(PUBLISHER.id)}/${"0193a000-0000-7000-8000-000000000000"}/public-key`]) {
    const r = await api(dave, "GET", url);
    expect([r.status, r.text], url).toEqual([404, ""]);
  }
  expect(await api(dave, "POST", REVISIONS, envelope(OPENAPI, `9.9.9-r${run}`)).then((r) => [r.status, r.text])).toEqual([404, ""]);

  // The budget (10 a minute per account): alice holds publisher briefly, so her budget is untouched by earlier steps.
  expect((await setRoles(alice, DEMO_ORG.id, aliceId, ["admin", "publisher"])).status).toBe(200);
  const statuses: number[] = [];
  for (let i = 0; i < 11; i++) statuses.push((await api(alice, "POST", REVISIONS, "{not json")).status);
  const limited = await api(alice, "POST", REVISIONS, "{not json");
  expect(statuses.slice(0, 10)).toEqual(Array(10).fill(400));
  expect(statuses[10]).toBe(429);
  expect(Number(limited.headers["retry-after"])).toBeGreaterThan(0);
  expect((await setRoles(alice, DEMO_ORG.id, aliceId, ["admin"])).status).toBe(200);
  Object.assign(record, { roles: matrix, nonMember: 404, rateLimit: { allowed: 10, eleventh: statuses[10] } });
  expect(PARTNER_ORG.id).not.toBe(DEMO_ORG.id);
  expect(daveId).toMatch(UUID);
});

test("8. Jaeger shows the publish, verify and operator spans, and nothing secret reaches telemetry", async ({ request }) => {
  test.setTimeout(240_000);
  let publishSpans: Awaited<ReturnType<typeof recentSpans>> = [];
  await expect
    .poll(
      async () => {
        publishSpans = (await recentSpans("gozleme-api", 20, "gozleme.manifest.publish")).filter((s) => s.name === "gozleme.manifest.publish");
        const outcomes = new Set(publishSpans.map((s) => String(s.attributes["gozleme.manifest.outcome"])));
        return ["published", "rejected", "version_exists", "forbidden", "not_found", "suspended", "rate_limited"].every((o) => outcomes.has(o));
      },
      { timeout: 90_000, intervals: [3000] },
    )
    .toBe(true);
  const verifySpans = (await recentSpans("gozleme-api", 20, "gozleme.manifest.verify")).filter((s) => s.name === "gozleme.manifest.verify");
  expect(verifySpans.some((s) => s.attributes["gozleme.manifest.verification_reason"] === "none")).toBe(true);
  expect(verifySpans.some((s) => s.attributes["gozleme.manifest.verification_reason"] === "native_digest_mismatch")).toBe(true);
  let operatorSpans: Awaited<ReturnType<typeof recentSpans>> = [];
  await expect
    .poll(async () => {
      operatorSpans = (await recentSpans("gozleme-operator", 20, "gozleme.operator.signing_key_succession")).filter((s) => s.name === "gozleme.operator.signing_key_succession");
      return operatorSpans.length;
    }, { timeout: 60_000, intervals: [3000] })
    .toBeGreaterThanOrEqual(2);
  for (const span of operatorSpans) expect(Object.keys(span.attributes).filter((k) => k.startsWith("gozleme."))).toEqual(["gozleme.operator.exit_code"]);
  const publishKeys = ["gozleme.manifest.family", "gozleme.manifest.outcome", "gozleme.organization.id", "gozleme.publisher.id", "gozleme.capability.id", "gozleme.revision.id"];
  for (const span of publishSpans) for (const key of Object.keys(span.attributes)) expect(publishKeys).toContain(key);

  // A publication started in the browser is one browser → web → API → PostgreSQL trace.
  let uiTraceId: string | undefined;
  await expect
    .poll(
      async () => {
        const ui = await recentSpans("gozleme-web-browser", 20, "gozleme.ui.manifest_publish");
        for (const candidate of ui.filter((s) => s.name === "gozleme.ui.manifest_publish")) {
          const trace = ui.filter((s) => s.traceId === candidate.traceId);
          if (trace.some((s) => s.service === "gozleme-api" && s.name === "gozleme.manifest.publish" && s.attributes["gozleme.manifest.outcome"] === "published") && trace.some((s) => s.service === "gozleme-web")) {
            uiTraceId = candidate.traceId;
            return true;
          }
        }
        return false;
      },
      { timeout: 60_000, intervals: [2000] },
    )
    .toBe(true);

  let metrics = "";
  await expect
    .poll(async () => {
      metrics = await (await request.get(METRICS_URL)).text();
      return metrics.includes("gozleme_manifest_publications_total{") && metrics.includes("gozleme_manifest_verifications_total{");
    }, { timeout: 90_000, intervals: [5000] })
    .toBe(true);
  const lines = metrics.split("\n").filter((l) => /^gozleme_manifest_(publications|verifications)_total\{/.test(l));
  for (const line of lines) {
    const labels = [...line.matchAll(/(\w+)="/g)].map((m) => m[1]!).filter((k) => k.startsWith("gozleme_")).sort();
    expect(labels).toEqual(line.startsWith("gozleme_manifest_publications") ? ["gozleme_manifest_family", "gozleme_manifest_outcome"] : ["gozleme_manifest_outcome", "gozleme_manifest_verification_reason"]);
  }

  const hex = (b: Buffer) => b.toString("hex");
  const privateValues = [
    KEK, ...secretStrings, DEMO.alice, DEMO.carol, DEMO.dave, DEMO_PASSWORD, ALIAS, ...secrets.names, ...secrets.versions,
    ...secrets.digests.map((d) => d.slice("sha256:".length)),
    ...secrets.statements.flatMap((s) => [s.toString("base64"), s.toString("utf8")]),
    ...secrets.signatures.flatMap((s) => [s.toString("base64"), hex(s), hex(s).toUpperCase()]),
  ];
  const shotTrace = async (traceId: string, span: string, file: string) => {
    const page = await alice.context().newPage();
    await page.setViewportSize({ width: 1600, height: 1000 });
    await page.goto(`${JAEGER_URL}/trace/${traceId}`);
    await expect(page.getByText(span).first()).toBeAttached({ timeout: 15_000 });
    await page.getByRole("switch", { name: new RegExp(`${span}$`) }).first().click();
    await expect(page.getByRole("heading", { name: span, level: 2 })).toBeVisible();
    await page.getByText(/^Attributes:?$/).first().click();
    for (const v of privateValues) await expect(page.locator("body")).not.toContainText(v);
    await page.screenshot({ path: `${shots}/${file}`, fullPage: true });
    await page.close();
  };
  await shotTrace(uiTraceId!, "gozleme.manifest.publish", "70-manifest-publish-trace-jaeger.png");
  const failed = verifySpans.find((s) => s.attributes["gozleme.manifest.verification_reason"] === "native_digest_mismatch")!;
  await shotTrace(failed.traceId, "gozleme.manifest.verify", "71-manifest-verify-failed-trace-jaeger.png");
  await shotTrace(operatorSpans[0]!.traceId, "gozleme.operator.signing_key_succession", "72-signing-key-succession-trace-jaeger.png");

  const logs = await storedLogs('{service_name=~"gozleme-.+"}', 20);
  const telemetry = JSON.stringify(publishSpans) + JSON.stringify(verifySpans) + JSON.stringify(operatorSpans) + lines.join("\n") + JSON.stringify(logs.filter((l) => l.at >= suiteStarted));
  for (const v of privateValues) expect(telemetry, "telemetry must not contain a private value").not.toContain(v);
  const composeLogs = execFileSync("docker", ["compose", "logs", "--no-color", "--since", new Date(suiteStarted).toISOString()], { cwd: repoRoot, maxBuffer: 64 * 1024 * 1024 }).toString();
  for (const v of privateValues) expect(composeLogs, "compose logs must not contain a private value").not.toContain(v);

  writeEvidence({
    ...record,
    observability: {
      spans: ["gozleme.manifest.publish", "gozleme.manifest.verify", "gozleme.operator.signing_key_succession"],
      publishOutcomes: [...new Set(publishSpans.map((s) => String(s.attributes["gozleme.manifest.outcome"])))].sort(),
      verificationReasons: [...new Set(verifySpans.map((s) => String(s.attributes["gozleme.manifest.verification_reason"])))].sort(),
      counters: ["gozleme_manifest_publications_total", "gozleme_manifest_verifications_total"],
      noSecretsInSpansMetricsOrLogs: true,
    },
  });
});

/** Merges into `revision-evidence.json` (the SC-007 timing from the integration suite stays). Opaque IDs only. */
function writeEvidence(fields: Record<string, unknown>) {
  const file = `${evidence}/revision-evidence.json`;
  const existing = existsSync(file) ? (JSON.parse(readFileSync(file, "utf8")) as Record<string, unknown>) : {};
  const merged = {
    ...existing,
    spec: "GOZ-0007",
    capturedAt: new Date().toISOString(),
    origin: ORIGIN,
    organization: DEMO_ORG.id,
    publisher: PUBLISHER.id,
    capability: { id: CAPABILITY.id, canonicalId: CANONICAL },
    demo: fields,
  };
  const text = JSON.stringify(merged, null, 2);
  for (const v of [DEMO.alice, DEMO.carol, DEMO.dave, DEMO_ORG.name, PARTNER_ORG.name, DEMO_PASSWORD, KEK, ...secretStrings]) expect(text).not.toContain(v);
  writeFileSync(file, `${text}\n`);
}
