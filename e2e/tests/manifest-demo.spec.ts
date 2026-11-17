import AxeBuilder from "@axe-core/playwright";
import { createHash } from "node:crypto";
import { execFileSync, spawnSync } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { expect, test, type Browser, type Page } from "@playwright/test";
import { API_URL } from "../playwright.config";
import { repoRoot, secretStrings } from "./compose";
import { DEMO, DEMO_ORG, DEMO_PASSWORD, PARTNER_ORG, evidenceDir, shotsDir } from "./evidence";
import { JAEGER_URL, METRICS_URL, recentSpans, storedLogs } from "./telemetry";

/**
 * GOZ-0006 autonomous demo (quickstart steps 1–7): carol validates every family's valid sample in the browser
 * and sees the envelope, the unchanged native document with its digest and the projection as three panels; every
 * invalid sample is rejected with exactly its expected problem and no synthetic secret on the page. Through the API
 * the native document comes back byte-identical and an extension member never changes the projection. alice
 * (admin) gets no control and 403, each role matches the contract row, dave gets the identical empty 404, a
 * reclaimed publisher answers 409 and the 31st validation in a minute 429. Nothing is stored, and the span and
 * both counters carry bounded labels only.
 */
const shots = shotsDir("goz-0006");
const evidence = evidenceDir("goz-0006");
const suiteStarted = Date.now();
const ORIGIN = process.env.GOZLEME_REGISTRY_ORIGIN ?? "registry.demo.gozleme.test";
const PUBLISHER = { id: "0193a000-0000-7000-8000-000000000010", handle: "seeded-demo" } as const;
const CAPABILITY = { id: "0193a000-0000-7000-8000-000000000011", name: "manifest-demo" } as const;
const CANONICAL = `gz://${ORIGIN}/cap/${CAPABILITY.id}`;
const ALIAS = `${PUBLISHER.handle}/${CAPABILITY.name}`;
const ORG_API = `${API_URL}/api/v1/organizations/${DEMO_ORG.id}`;
const PARTNER_API = `${API_URL}/api/v1/organizations/${PARTNER_ORG.id}`;
const VALIDATE = `${ORG_API}/publishers/${PUBLISHER.id}/capabilities/${CAPABILITY.id}/manifests/validate`;
const ROLES = ["admin", "approver", "member", "publisher", "reviewer"] as const;
const run = String(Math.floor(100000 + Math.random() * 900000));
const SQUATTED = `mfsquat-${run}`;

const SAMPLES = `${repoRoot}web/src/samples/manifests`;
type Sample = { id: string; family: string; title: string; file: string; expected: { outcome: string; problems?: { category: string; rule: string; location: unknown }[] } };
const index = JSON.parse(readFileSync(`${SAMPLES}/index.json`, "utf8")) as { samples: Sample[] };
const syntheticSecrets = JSON.parse(readFileSync(`${SAMPLES}/synthetic-secrets.json`, "utf8")) as string[];
const valid = index.samples.filter((s) => s.expected.outcome === "accepted");
const invalid = index.samples.filter((s) => s.expected.outcome === "rejected");
const sampleText = (s: Sample, capability = CANONICAL) => readFileSync(`${SAMPLES}/${s.file}`, "utf8").replaceAll("{{capability}}", capability);
const nativeDocument = (s: Sample) => (JSON.parse(readFileSync(`${SAMPLES}/${s.file}`, "utf8")) as { native: { document: string } }).native.document;
/** Native names and titles from the samples: never in telemetry. */
const NATIVE_NAMES = ["io.example.demo/forecasts", "io.example.demo/forecasts-local", "Forecast Agent", "Forecasts API", "Forecast Events", "forecast-summary", "forecast-small", "get_forecast"];

let alice: Page;
let carol: Page;
let dave: Page;
let aliceId: string;
let carolId: string;
let daveId: string;
let rowsBefore = "";
const record: Record<string, unknown> = {};

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
  const text = await res.text();
  return { status: res.status(), text, cache: res.headers()["cache-control"], retryAfter: res.headers()["retry-after"] };
}

/** Validates through the API; a 429 from an earlier step's budget waits out the window (step 5 tests the limit itself). */
async function validate(page: Page, body: string, url = VALIDATE) {
  for (;;) {
    const r = await api(page, "POST", url, body);
    if (r.status !== 429) return r;
    await page.waitForTimeout((Number(r.retryAfter) + 1) * 1000);
  }
}

const setRoles = (page: Page, org: string, userId: string, roles: string[]) => api(page, "PUT", `${API_URL}/api/v1/organizations/${org}/members/${userId}/roles`, JSON.stringify({ roles }));
const userId = async (page: Page) => String((await (await page.request.get(`${API_URL}/api/v1/auth/session`)).json()).userId);
const sha256 = (text: string) => `sha256:${createHash("sha256").update(Buffer.from(text, "utf8")).digest("hex")}`;

function psql(sql: string): string {
  return execFileSync("docker", ["compose", "exec", "-T", "postgres", "psql", "-X", "-q", "-At", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "gozleme", "-f", "-"], {
    cwd: repoRoot,
    input: sql,
    stdio: "pipe",
  })
    .toString()
    .trim();
}

/** Row counts of every `gozleme` table and the migration history, as the superuser (SC-007). */
function rowCounts(): string {
  const tables = psql("SELECT string_agg(tablename, ',' ORDER BY tablename) FROM pg_tables WHERE schemaname = 'gozleme'").split(",");
  const counts = tables.map((t) => `(SELECT '${t}=' || count(*) FROM gozleme."${t}")`).join(" || ',' || ");
  return psql(`SELECT ${counts} || ',history=' || (SELECT count(*) FROM public."__EFMigrationsHistory");`);
}

function migrate(args: string[]) {
  const r = spawnSync("docker", ["compose", "run", "--rm", "--no-deps", "migrate", ...args], { cwd: repoRoot, encoding: "utf8" });
  return { exit: r.status ?? -1, stdout: r.stdout.trim(), stderr: r.stderr.trim() };
}

/** The UI's location text for an expected location (`ProblemList` → `locationText`). */
function locationText(location: unknown): string {
  if (location === null) return "—";
  const l = location as { part: string; pointer?: string; line?: number; column?: number };
  if (l.pointer !== undefined) return `${l.part} ${l.pointer === "" ? "(root)" : l.pointer}`;
  return `${l.part} line ${l.line}, column ${l.column}`;
}

async function openValidator(page: Page) {
  await page.goto(`/organizations/${DEMO_ORG.id}/publishers/${PUBLISHER.id}`);
  const row = page.getByTestId("capability-row").filter({ hasText: ALIAS });
  await row.getByRole("link", { name: "Validate manifest" }).click();
  await expect(page.getByTestId("capability-canonical-id")).toHaveText(CANONICAL);
}

async function validateInUi(page: Page, sample: Sample) {
  await page.getByTestId(`family-${sample.family}`).check();
  await page.getByTestId("sample-select").selectOption(sample.id);
  await page.getByTestId("load-sample").click();
  await expect(page.getByTestId("manifest-text")).not.toHaveValue("");
  expect(await page.getByTestId("manifest-text").inputValue()).toContain(`"capability": "${CANONICAL}"`);
  await page.getByTestId("validate").click();
}

test.afterAll(async () => {
  // Restore the seeded roles even if a step failed midway: alice {admin, member}, carol {member, publisher}, dave admin.
  if (alice && aliceId) await setRoles(alice, DEMO_ORG.id, aliceId, ["admin"]);
  if (alice && carolId) await setRoles(alice, DEMO_ORG.id, carolId, ["publisher"]);
  if (dave && daveId) await setRoles(dave, PARTNER_ORG.id, daveId, ["admin"]);
  for (const page of [alice, carol, dave]) await page?.context().close();
});

test("a publisher member validates every family's valid sample and sees three separate parts", async ({ browser }) => {
  test.setTimeout(240_000);
  alice = await signedInPage(browser, DEMO.alice);
  carol = await signedInPage(browser, DEMO.carol);
  dave = await signedInPage(browser, DEMO.dave);
  [aliceId, carolId, daveId] = [await userId(alice), await userId(carol), await userId(dave)];
  // The GOZ-0005 demo before this one restores carol to publisher; make sure, then take the baseline (SC-007).
  expect((await setRoles(alice, DEMO_ORG.id, carolId, ["publisher"])).status).toBe(200);
  rowsBefore = rowCounts();

  const started = Date.now();
  await openValidator(carol);
  await expect(carol.getByTestId("family-picker").getByRole("radio")).toHaveCount(6);
  await expectAccessible(carol);
  const digests: Record<string, string> = {};
  for (const [i, sample] of valid.entries()) {
    await validateInUi(carol, sample);
    const accepted = carol.getByTestId("manifest-accepted");
    await expect(accepted).toBeVisible();
    await expect(carol.getByTestId("panel-envelope").getByRole("heading", { level: 2 })).toHaveText("Envelope");
    await expect(carol.getByTestId("panel-native").getByRole("heading", { level: 2 })).toHaveText("Native document");
    await expect(carol.getByTestId("panel-projection").getByRole("heading", { level: 2 })).toHaveText("Projection");
    await expect(carol.getByTestId("projection-version")).toContainText("projection v1");
    const document = nativeDocument(sample);
    await expect(carol.getByTestId("native-digest")).toHaveText(sha256(document));
    expect(await carol.getByTestId("native-document").textContent()).toBe(document);
    if (sample.family === "mcp") {
      const mode = sample.id.includes("2026-07-28") ? "stateless" : "stateful";
      await expect(carol.getByTestId("attribute-mode")).toContainText(`mode: ${mode}`);
      await expect(carol.getByTestId("attribute-mode").getByTestId("derived-marker")).toBeVisible();
    }

    digests[sample.id] = sha256(document);
    await carol.getByTestId("manifest-result").scrollIntoViewIfNeeded();
    await carol.screenshot({ path: `${shots}/${String(i + 1).padStart(2, "0")}-accepted-${sample.id}.png`, fullPage: true });
  }

  const elapsed = (Date.now() - started) / 1000;
  expect(elapsed).toBeLessThan(180); // SC-002
  await expectAccessible(carol);
  Object.assign(record, { validFamilies: { samples: valid.map((s) => s.id), elapsedSeconds: Math.round(elapsed), limitSeconds: 180, digests } });
});

test("every invalid sample is rejected with its expected problem and no secret on the page", async () => {
  test.setTimeout(240_000);
  await openValidator(carol);
  const rejected: Record<string, string> = {};
  for (const [i, sample] of invalid.entries()) {
    await validateInUi(carol, sample);
    const list = carol.getByTestId("problem-list");
    await expect(list).toBeVisible();
    const expected = sample.expected.problems!;
    const problems = list.getByTestId("problem");
    await expect(problems).toHaveCount(expected.length);
    for (const [n, e] of expected.entries()) {
      const problem = problems.nth(n);
      await expect(problem.getByTestId("problem-category")).toHaveText(e.category);
      await expect(problem.getByTestId("problem-rule")).toHaveText(e.rule);
      await expect(problem.getByTestId("problem-location")).toHaveText(locationText(e.location));
      await expect(problem.getByTestId("problem-message")).not.toBeEmpty();
    }
    // The result region and the page text never show a submitted value (the input box holds what was typed).
    const shown = (await carol.getByTestId("manifest-result").innerText()) + (await carol.locator("body").textContent());
    for (const secret of syntheticSecrets) expect(shown, `${sample.id} must not show a synthetic secret`).not.toContain(secret);
    rejected[sample.id] = expected.map((e) => `${e.category} ${e.rule}`).join(" + ");
    await list.scrollIntoViewIfNeeded();
    await carol.screenshot({ path: `${shots}/${String(20 + i).padStart(2, "0")}-rejected-${sample.id}.png` });
  }

  await expectAccessible(carol);
  Object.assign(record, { rejections: rejected });
});

test("through the API the native document is unchanged and extensions never change the projection", async () => {
  test.setTimeout(300_000);
  const boundary: Record<string, unknown> = {};
  for (const sample of valid) {
    const body = sampleText(sample);
    const first = await validate(carol, body);
    expect(first.status).toBe(200);
    expect(first.cache).toContain("no-store");
    const result = JSON.parse(first.text) as { native: { document: string; digest: string }; projection: unknown; envelope: Record<string, unknown> };
    expect(result.native.document).toBe(nativeDocument(sample));
    expect(result.native.digest).toBe(sha256(nativeDocument(sample)));
    expect(result.envelope).not.toHaveProperty("document");

    // An extension member appended at the native root (MCP and model: _meta / x-, OpenAPI and AsyncAPI: x-, skill: metadata, A2A: params).
    const envelope = JSON.parse(body) as { native: { document: string } };
    envelope.native.document = withExtension(sample, envelope.native.document);
    const extended = JSON.parse((await validate(carol, JSON.stringify(envelope))).text) as typeof result;
    expect(extended.native.digest).not.toBe(result.native.digest);
    expect(extended.projection).toEqual(result.projection);
    expect(JSON.stringify(extended.projection)).not.toContain("ZZEXTENSIONZZ");
    boundary[sample.id] = { documentUnchanged: true, digestMatchesLocalSha256: true, extensionChangesDigestOnly: true };
  }

  // An alias claim is the same capability; a foreign claim is identity_mismatch with no existence oracle.
  const byAlias = JSON.parse((await validate(carol, sampleText(valid[0]!, ALIAS))).text) as { outcome: string };
  expect(byAlias.outcome).toBe("accepted");
  expect(rowCounts()).toBe(rowsBefore);
  Object.assign(record, { apiBoundary: boundary, aliasClaimAccepted: true, rowsUnchangedAfterValidations: true });
});

function withExtension(sample: Sample, document: string): string {
  switch (sample.family) {
    case "skill":
      return document.replace("  author-team: demo", "  author-team: demo\n  zz-extension: ZZEXTENSIONZZ");
    case "api":
      if (sample.file.endsWith("3.1.json")) return document.replace("  x-audience: internal", "  x-audience: internal\n  x-zz: ZZEXTENSIONZZ");
      break;
    default:
      break;
  }

  const doc = JSON.parse(document) as Record<string, Record<string, unknown>>;
  if (sample.family === "mcp") doc._meta = { ...doc._meta, "io.example.demo/zz": "ZZEXTENSIONZZ" };
  else if (sample.family === "a2a") (doc.capabilities.extensions as { params: Record<string, string> }[])[0]!.params.zz = "ZZEXTENSIONZZ";
  else (doc as Record<string, unknown>)["x-zz"] = "ZZEXTENSIONZZ";
  return `${JSON.stringify(doc, null, 2)}\n`;
}

test("roles, non-members, suspension and the validation budget answer in the contract order", async () => {
  test.setTimeout(300_000);
  // alice (admin) sees no control; a direct POST is 403 and her budget is untouched.
  await alice.goto(`/organizations/${DEMO_ORG.id}/publishers/${PUBLISHER.id}`);
  await expect(alice.getByTestId("capability-row").filter({ hasText: ALIAS })).toBeVisible();
  await expect(alice.getByTestId("validate-manifest-link")).toHaveCount(0);
  const forbidden = await api(alice, "POST", VALIDATE, sampleText(valid[0]!));
  expect(`${forbidden.status} ${forbidden.text}`).toBe('403 {"error":"forbidden"}');
  await alice.goto(`/organizations/${DEMO_ORG.id}/publishers/${PUBLISHER.id}/capabilities/${CAPABILITY.id}/manifest`);
  await expect(alice.getByTestId("permission-notice")).toBeVisible();
  await expect(alice.getByTestId("manifest-form")).toHaveCount(0);
  await expectAccessible(alice);
  await alice.screenshot({ path: `${shots}/40-admin-without-publisher-forbidden.png`, fullPage: true });

  // Each built-in role alone against the contract row: publisher allow, every other role deny.
  const matrix: Record<string, number> = {};
  for (const role of ROLES) {
    expect((await setRoles(alice, DEMO_ORG.id, carolId, [role])).status).toBe(200);
    const r = role === "publisher" ? await validate(carol, "{}") : await api(carol, "POST", VALIDATE, "{not json");
    matrix[role] = r.status;
    expect(r.status, role).toBe(role === "publisher" ? 200 : 403);
  }
  expect((await setRoles(alice, DEMO_ORG.id, carolId, ["publisher"])).status).toBe(200);

  // dave (Seeded Partner Org) gets the identical empty 404 for Seeded Demo Org's IDs and for unknown ones.
  const nonMember = await api(dave, "POST", VALIDATE, sampleText(valid[0]!));
  const unknown = await api(dave, "POST", `${API_URL}/api/v1/organizations/0193a000-0000-7000-8000-0000000000ee/publishers/${PUBLISHER.id}/capabilities/${CAPABILITY.id}/manifests/validate`, "{}");
  expect(`${nonMember.status}|${nonMember.text}|${nonMember.cache}`).toBe(`${unknown.status}|${unknown.text}|${unknown.cache}`);
  expect(nonMember.status).toBe(404);
  expect(nonMember.text).toBe("");

  // A publisher suspended by handle-reclaim answers 409 before its body is read.
  expect((await setRoles(dave, PARTNER_ORG.id, daveId, ["admin", "publisher"])).status).toBe(200);
  const squat = JSON.parse((await api(dave, "POST", `${PARTNER_API}/publishers`, JSON.stringify({ handle: SQUATTED, displayName: "Squat" }))).text) as { id: string };
  const squatCapability = JSON.parse((await api(dave, "POST", `${PARTNER_API}/publishers/${squat.id}/capabilities`, JSON.stringify({ name: "forecast", displayName: "F" }))).text) as { id: string };
  const reclaim = migrate(["handle-reclaim", "--handle", SQUATTED, "--organization", DEMO_ORG.id]);
  expect(reclaim.exit, reclaim.stderr).toBe(0);
  const suspendedUrl = `${PARTNER_API}/publishers/${squat.id}/capabilities/${squatCapability.id}/manifests/validate`;
  const suspended = await api(dave, "POST", suspendedUrl, "{not json");
  expect(`${suspended.status} ${suspended.text}`).toBe('409 {"error":"suspended"}');
  await dave.goto(`/organizations/${PARTNER_ORG.id}/publishers/${squat.id}/capabilities/${squatCapability.id}/manifest`);
  await expect(dave.getByTestId("suspended-notice")).toBeVisible();
  await expect(dave.getByTestId("manifest-form")).toHaveCount(0);
  await dave.screenshot({ path: `${shots}/41-suspended-publisher.png`, fullPage: true });
  expect((await setRoles(dave, PARTNER_ORG.id, daveId, ["admin"])).status).toBe(200);

  // 31 validations inside a minute: the 31st is 429 with Retry-After (alice holds publisher only for this step,
  // and her earlier 403 took no permit).
  expect((await setRoles(alice, DEMO_ORG.id, aliceId, ["admin", "publisher"])).status).toBe(200);
  const statuses: number[] = [];
  let limited: Awaited<ReturnType<typeof api>> | undefined;
  for (let i = 0; i < 31; i++) {
    const r = await api(alice, "POST", VALIDATE, "{}");
    statuses.push(r.status);
    if (r.status === 429) limited = r;
  }

  expect((await setRoles(alice, DEMO_ORG.id, aliceId, ["admin"])).status).toBe(200);
  expect(statuses.slice(0, 30).every((s) => s === 200)).toBe(true);
  expect(statuses[30]).toBe(429);
  expect(limited!.text).toBe('{"error":"rate_limited"}');
  expect(Number(limited!.retryAfter)).toBeGreaterThan(0);
  expect(Number(limited!.retryAfter)).toBeLessThanOrEqual(60);

  Object.assign(record, {
    adminWithoutPublisher: "403 forbidden, no control shown",
    roleMatrix: matrix,
    nonMemberAndUnknownOrganization: "404 (identical, empty, no-store)",
    suspendedByHandleReclaim: "409 suspended (body not read)",
    rateLimit: { allowed: 30, thirty_first: 429, retryAfterSeconds: Number(limited!.retryAfter) },
  });
});

test("observability: validations are traced and counted with bounded labels and no content", async ({ request }) => {
  test.setTimeout(240_000);
  let spans: Awaited<ReturnType<typeof recentSpans>> = [];
  await expect
    .poll(
      async () => {
        spans = (await recentSpans("gozleme-api", 20, "gozleme.manifest.validate")).filter((s) => s.name === "gozleme.manifest.validate");
        const outcomes = new Set(spans.map((s) => String(s.attributes["gozleme.manifest.outcome"])));
        return ["accepted", "rejected", "forbidden", "not_found", "suspended", "rate_limited"].every((o) => outcomes.has(o));
      },
      { timeout: 90_000, intervals: [3000] },
    )
    .toBe(true);
  const allowedKeys = ["gozleme.manifest.family", "gozleme.manifest.outcome", "gozleme.manifest.problem_count", "gozleme.organization.id", "gozleme.publisher.id", "gozleme.capability.id"];
  for (const span of spans) for (const key of Object.keys(span.attributes)) expect(allowedKeys).toContain(key);
  const accepted = spans.find((s) => s.attributes["gozleme.manifest.outcome"] === "accepted" && s.attributes["gozleme.capability.id"] === CAPABILITY.id)!;
  expect(accepted.attributes["gozleme.organization.id"]).toBe(DEMO_ORG.id);
  const notFound = spans.find((s) => s.attributes["gozleme.manifest.outcome"] === "not_found")!;
  expect(notFound.attributes["gozleme.organization.id"]).toBeUndefined();

  let metrics = "";
  await expect
    .poll(
      async () => {
        metrics = await (await request.get(METRICS_URL)).text();
        const validations = metrics.split("\n").filter((l) => l.startsWith("gozleme_manifest_validations_total{"));
        const problems = metrics.split("\n").filter((l) => l.startsWith("gozleme_manifest_problems_total{"));
        return ["accepted", "rejected", "rate_limited"].every((o) => validations.some((l) => l.includes(`gozleme_manifest_outcome="${o}"`))) && problems.length > 0;
      },
      { timeout: 90_000, intervals: [5000] },
    )
    .toBe(true);
  const lines = metrics.split("\n").filter((l) => /^gozleme_manifest_(validations|problems)_total\{/.test(l));
  for (const line of lines) {
    const labels = [...line.matchAll(/(\w+)="/g)].map((m) => m[1]).filter((k) => k!.startsWith("gozleme_"));
    expect(labels.sort()).toEqual(line.startsWith("gozleme_manifest_validations") ? ["gozleme_manifest_family", "gozleme_manifest_outcome"] : ["gozleme_manifest_category", "gozleme_manifest_family"]);
  }

  // The rate limit is recorded on the manifest span and counter, never as an auth failure.
  const authLimited = metrics.split("\n").filter((l) => l.startsWith("gozleme_auth") && l.includes('="rate_limited"'));
  const logs = await storedLogs('{service_name=~"gozleme-.+"}', 20);
  const page = await alice.context().newPage();
  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.goto(`${JAEGER_URL}/trace/${accepted.traceId}`);
  await expect(page.getByText("gozleme.manifest.validate").first()).toBeAttached({ timeout: 15_000 });
  await page.getByRole("switch", { name: "gozleme-api gozleme.manifest.validate" }).first().click();
  await expect(page.getByRole("heading", { name: "gozleme.manifest.validate", level: 2 })).toBeVisible();
  const privateValues = [CAPABILITY.name, ALIAS, ...NATIVE_NAMES, ...syntheticSecrets, ...secretStrings, DEMO.alice, DEMO.carol, DEMO.dave, DEMO_PASSWORD, "/tools/0/execution", "/remotes/0/headers/0/value"];
  for (const v of privateValues) await expect(page.locator("body")).not.toContainText(v);
  await page.screenshot({ path: `${shots}/50-manifest-validate-trace-jaeger.png`, fullPage: true });
  await page.close();

  const digests = Object.values((record.validFamilies as { digests: Record<string, string> }).digests).map((d) => d.slice("sha256:".length));
  const telemetry = JSON.stringify(spans) + lines.join("\n") + JSON.stringify(logs.filter((l) => l.at >= suiteStarted));
  for (const v of [...privateValues, ...digests]) expect(telemetry, `telemetry must not contain ${v}`).not.toContain(v);
  const composeLogs = execFileSync("docker", ["compose", "logs", "--no-color", "--since", new Date(suiteStarted).toISOString()], { cwd: repoRoot, maxBuffer: 64 * 1024 * 1024 }).toString();
  for (const v of [...syntheticSecrets, ...NATIVE_NAMES, ALIAS, ...digests]) expect(composeLogs, `compose logs must not contain ${v}`).not.toContain(v);

  // Boundary: no catalog, publish or stored-manifest route over HTTP.
  const boundary: Record<string, number> = {};
  for (const [method, url] of [
    ["GET", `${API_URL}/api/v1/capabilities`],
    ["POST", `${API_URL}/api/v1/publish`],
    ["GET", `${ORG_API}/publishers/${PUBLISHER.id}/capabilities/${CAPABILITY.id}/manifests`],
    ["GET", VALIDATE],
  ] as const) {
    const { status } = await api(alice, method, url, method === "GET" ? undefined : "{}");
    expect([404, 405], `${method} ${url}`).toContain(status);
    boundary[`${method} ${url.replace(API_URL, "")}`] = status;
  }

  writeEvidence({
    ...record,
    observability: {
      span: "gozleme.manifest.validate",
      spanOutcomes: [...new Set(spans.map((s) => String(s.attributes["gozleme.manifest.outcome"])))].sort(),
      counters: ["gozleme_manifest_validations_total", "gozleme_manifest_problems_total"],
      authRateLimitedSeries: authLimited.length,
      noContentInSpansMetricsOrLogs: true,
    },
    boundary,
  });
});

/** Merges into `manifest-evidence.json` (the SC-006 timing from the integration suite stays). Opaque IDs only. */
function writeEvidence(fields: Record<string, unknown>) {
  const file = `${evidence}/manifest-evidence.json`;
  const existing = existsSync(file) ? (JSON.parse(readFileSync(file, "utf8")) as Record<string, unknown>) : {};
  const merged = {
    ...existing,
    spec: "GOZ-0006",
    capturedAt: new Date().toISOString(),
    origin: ORIGIN,
    organization: DEMO_ORG.id,
    publisher: PUBLISHER.id,
    capability: { id: CAPABILITY.id, canonicalId: CANONICAL },
    demo: fields,
  };
  const text = JSON.stringify(merged, null, 2);
  for (const v of [DEMO.alice, DEMO.carol, DEMO.dave, DEMO_ORG.name, PARTNER_ORG.name, DEMO_PASSWORD, ...secretStrings, ...syntheticSecrets]) expect(text).not.toContain(v);
  writeFileSync(file, `${text}\n`);
}
