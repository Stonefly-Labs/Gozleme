import AxeBuilder from "@axe-core/playwright";
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { expect, test, type Browser, type Page } from "@playwright/test";
import { API_URL } from "../playwright.config";
import { repoRoot, secretStrings } from "./compose";
import { attempt, execute, psql } from "./db";
import { DEMO, DEMO_ORG, DEMO_PASSWORD, PARTNER_ORG, evidenceDir, shotsDir } from "./evidence";
import { projectionRegenerate } from "./operator";
import { JAEGER_URL, METRICS_URL, recentSpans, storedLogs } from "./telemetry";

/**
 * GOZ-0008 autonomous demo (quickstart "Demo walkthrough" steps 1–6). carol publishes an MCP manifest in the browser and
 * browses its four children; a second revision keeps, retitles, drops and adds entries, and the dropped prompt shows
 * **Absent**; an `a2a` card with a repeated skill `id` is accepted by validate and refused by publish (`422`) with
 * nothing stored; `projection-regenerate` adds an unattested generation 2 with every child ID kept; child, generation
 * and revision IDs never stand in for each other; SQL mutation as both logins is refused, dave and a signed-out visitor
 * see nothing; GOZ-0007's revisions have generation 1 and children and still verify; Jaeger shows the assign,
 * generation and operator spans, and no native name, title or digest reaches telemetry or logs.
 */
const shots = shotsDir("goz-0008");
const evidence = evidenceDir("goz-0008");
const suiteStarted = Date.now();
const ORIGIN = process.env.GOZLEME_REGISTRY_ORIGIN ?? "registry.demo.gozleme.test";
const PUBLISHER = { id: "0193a000-0000-7000-8000-000000000010", handle: "seeded-demo" } as const;
const CAPABILITY = { id: "0193a000-0000-7000-8000-000000000011", name: "manifest-demo" } as const;
const CANONICAL = `gz://${ORIGIN}/cap/${CAPABILITY.id}`;
const ALIAS = `${PUBLISHER.handle}/${CAPABILITY.name}`;
const ORG_API = `${API_URL}/api/v1/organizations/${DEMO_ORG.id}`;
const CAP_API = `${ORG_API}/publishers/${PUBLISHER.id}/capabilities/${CAPABILITY.id}`;
const REVISIONS = `${CAP_API}/revisions`;
const VALIDATE = `${CAP_API}/manifests/validate`;
const CHILDREN = `${CAP_API}/children`;
const TABLES = ["capability_children", "revision_children", "projection_generations"];
const UUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/;
const CHILD_ID = new RegExp(`^${CANONICAL.replaceAll(".", "\\.")}/(tool|resource|prompt|skill)/${UUID.source}$`);
const NOTICE = "Children are declared by the publisher in the manifest. They are not verified behaviour.";
// Revisions and children are permanent, so every run publishes run-unique versions and native names (research R14).
const run = String(Math.floor(100000 + Math.random() * 900000));
const V1 = `2.0.0-r${run}`;
const V2 = `2.1.0-r${run}`;
const N = { search: `search_r${run}`, fetch: `fetch_r${run}`, docs: `docs_r${run}`, summarize: `summarize_r${run}`, index: `index_r${run}` };
const T = { search: `Search ${run}`, fetch: `Fetch ${run}`, fetch2: `Fetch again ${run}`, docs: `Docs ${run}` };

const SAMPLES = `${repoRoot}web/src/samples/manifests`;
type Sample = { id: string; family: string; file: string };
const index = JSON.parse(readFileSync(`${SAMPLES}/index.json`, "utf8")) as { samples: Sample[] };
const sample = (id: string) => JSON.parse(readFileSync(`${SAMPLES}/${index.samples.find((s) => s.id === id)!.file}`, "utf8").replaceAll("{{capability}}", CANONICAL)) as Record<string, any>;

type Entry = { kind: "tool" | "resource" | "prompt"; name: string; title?: string };
/** An MCP 2026-07-28 envelope declaring exactly `entries`. */
function mcp(version: string, entries: Entry[]) {
  const env = sample("valid-stateless-2026-07-28");
  const doc = JSON.parse(env.native.document) as Record<string, unknown>;
  const of = (kind: Entry["kind"]) => entries.filter((e) => e.kind === kind);
  doc.tools = of("tool").map((e) => ({ name: e.name, ...(e.title ? { title: e.title } : {}), inputSchema: { type: "object" } }));
  doc.resources = of("resource").map((e, i) => ({ name: e.name, ...(e.title ? { title: e.title } : {}), uri: `https://children.example.test/r${run}/${i}` }));
  doc.prompts = of("prompt").map((e) => ({ name: e.name, ...(e.title ? { title: e.title } : {}) }));
  env.version = version;
  env.native.document = JSON.stringify(doc, null, 2);
  return JSON.stringify(env, null, 2);
}

/** An A2A card whose first skill `id` appears twice (GOZ-0006-IF-07): validate accepts it, publish refuses it. */
function a2aDuplicate(version: string) {
  const env = sample("valid-agent-card");
  const card = JSON.parse(env.native.document) as { skills: Record<string, unknown>[] };
  card.skills.push({ ...card.skills[0]!, name: `Repeated skill ${run}` });
  env.version = version;
  env.native.document = JSON.stringify(card, null, 2);
  return JSON.stringify(env, null, 2);
}

let alice: Page;
let carol: Page;
let dave: Page;
let carolId: string;
const record: Record<string, unknown> = {};
let first: { id: string; sequence: number; children: Record<string, string> };
let second: { id: string; children: Record<string, string> };
let generation2: string;

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
  const text = (await res.body()).toString("utf8");
  return { status: res.status(), text, headers: res.headers(), json: () => JSON.parse(text) as Record<string, any> };
}

/** Publishes through the API; a 429 from the shared per-minute budget waits out the window. */
async function publish(page: Page, body: string) {
  for (;;) {
    const r = await api(page, "POST", REVISIONS, body);
    if (r.status !== 429) return r;
    await page.waitForTimeout((Number(r.headers["retry-after"]) + 1) * 1000);
  }
}

/** The publication budget is per clock minute: start browser publications at the top of a fresh minute. */
async function freshMinute(page: Page) {
  const second = Number(psql("postgres", "SELECT extract(second FROM now())").out);
  await page.waitForTimeout(Math.max(0, 61 - second) * 1000);
}

const userId = async (page: Page) => String((await (await page.request.get(`${API_URL}/api/v1/auth/session`)).json()).userId);
const pagePath = `/organizations/${DEMO_ORG.id}/publishers/${PUBLISHER.id}/capabilities/${CAPABILITY.id}`;
const byName = (items: { nativeName: string; childId: string }[]) => Object.fromEntries(items.map((i) => [i.nativeName, i.childId]));
const revisionChildren = async (revisionId: string) => (await api(carol, "GET", `${REVISIONS}/${revisionId}/children`)).json().items as Record<string, any>[];
const count = (sql: string) => Number(psql("postgres", sql).out);

async function shot(page: Page, file: string) {
  await page.screenshot({ path: `${shots}/${file}`, fullPage: true });
}

/** Opens the manifest page, validates `body` and publishes it in the browser. */
async function publishInBrowser(body: string) {
  await carol.goto(`/organizations/${DEMO_ORG.id}/publishers/${PUBLISHER.id}`);
  await carol.getByTestId("capability-row").filter({ hasText: ALIAS }).getByTestId("validate-manifest-link").click();
  await expect(carol.getByTestId("capability-canonical-id")).toHaveText(CANONICAL);
  await carol.getByTestId("manifest-text").fill(body);
  await carol.getByTestId("validate").click();
  await expect(carol.getByTestId("manifest-accepted")).toBeVisible();
  await carol.getByTestId("publish").click();
}

test.afterAll(async () => {
  for (const page of [alice, carol, dave]) await page?.context().close();
});

test("1. a member browses a revision's four children, the Children view and a child page", async ({ browser }) => {
  test.setTimeout(240_000);
  alice = await signedInPage(browser, DEMO.alice);
  carol = await signedInPage(browser, DEMO.carol);
  dave = await signedInPage(browser, DEMO.dave);
  carolId = await userId(carol);

  await freshMinute(carol);
  await publishInBrowser(
    mcp(V1, [
      { kind: "tool", name: N.search, title: T.search },
      { kind: "tool", name: N.fetch, title: T.fetch },
      { kind: "resource", name: N.docs, title: T.docs },
      { kind: "prompt", name: N.summarize },
    ]),
  );
  await carol.waitForURL(new RegExp(`${pagePath}/revisions/${UUID.source}$`));
  const revisionId = carol.url().split("/").pop()!;

  // The revision page lists four children, each linked by its gz://…/cap/<uuid>/<kind>/<uuid> ID.
  const rows = carol.getByTestId("revision-child-row");
  await expect(rows).toHaveCount(4);
  await expect(rows.getByTestId("revision-child-name")).toHaveText([N.search, N.fetch, N.docs, N.summarize]);
  await expect(rows.getByTestId("revision-child-kind")).toHaveText(["Tool", "Tool", "Resource", "Prompt"]);
  for (const id of await rows.getByTestId("revision-child-canonical-id").allTextContents()) expect(id).toMatch(CHILD_ID);
  await expect(carol.getByTestId("revision-children").getByTestId("declared-notice")).toHaveText(NOTICE);
  await expect(carol.getByTestId("verification-badge")).toHaveAttribute("data-status", "verified");
  await carol.getByTestId("revision-children").scrollIntoViewIfNeeded();
  await expectAccessible(carol);
  await shot(carol, "01-revision-children.png");

  const items = await revisionChildren(revisionId);
  expect(items.map((i) => i.canonicalId)).toEqual(await rows.getByTestId("revision-child-canonical-id").allTextContents());
  first = { id: revisionId, sequence: (await api(carol, "GET", `${REVISIONS}/${revisionId}`)).json().sequence, children: byName(items as any) };
  expect(new Set(Object.values(first.children)).size).toBe(4);

  // The capability's Children view (from the revisions list), with the fixed notice and no badge.
  await carol.goto(`${pagePath}/revisions`);
  await carol.getByTestId("revisions-children-link").click();
  await carol.waitForURL(new RegExp(`${pagePath}/children$`));
  await expect(carol.getByTestId("declared-notice")).toHaveText(NOTICE);
  await expect(carol.getByTestId("child-list").locator(".badge")).toHaveCount(0);
  const listed: string[] = [];
  for (let after: number | null = 0; after !== null; ) {
    const page = (await api(carol, "GET", `${CHILDREN}?limit=100&after=${after}`)).json();
    listed.push(...page.items.map((c: { id: string }) => c.id));
    after = page.next;
  }
  for (const id of Object.values(first.children)) expect(listed).toContain(id);
  await expectAccessible(carol);
  await shot(carol, "02-children-view.png");

  // A child page (two moves from the revision page): kind, child ID, parent capability and history.
  await carol.goto(`${pagePath}/revisions/${revisionId}`);
  await carol.getByTestId("revision-child-row").first().getByTestId("revision-child-link").click();
  await carol.waitForURL(new RegExp(`${pagePath}/children/${first.children[N.search]}$`));
  await expect(carol.getByTestId("child-kind")).toHaveText("Tool");
  await expect(carol.getByTestId("child-capability-id")).toHaveText(CANONICAL);
  await expect(carol.getByTestId("declared-notice")).toHaveText(NOTICE);
  const api1 = (await api(carol, "GET", `${CHILDREN}/${first.children[N.search]}`)).json();
  expect(api1.child.canonicalId).toBe(`${CANONICAL}/tool/${first.children[N.search]}`);
  expect(api1.declaredNotVerified).toBe(true);
  await expectAccessible(carol);
  await shot(carol, "03-child-page.png");
  Object.assign(record, { browse: { revision: revisionId, children: Object.values(first.children) } });
});

test("2. kept and retitled children keep their IDs, a dropped one is absent, and duplicate keys are refused at publish", async () => {
  test.setTimeout(240_000);
  const r = await publish(
    carol,
    mcp(V2, [
      { kind: "tool", name: N.search, title: T.search },
      { kind: "tool", name: N.fetch, title: T.fetch2 },
      { kind: "tool", name: N.index },
      { kind: "resource", name: N.docs, title: T.docs },
    ]),
  );
  expect(r.status, r.text).toBe(201);
  second = { id: r.json().id, children: byName((await revisionChildren(r.json().id)) as any) };
  expect(second.children[N.search]).toBe(first.children[N.search]);
  expect(second.children[N.fetch]).toBe(first.children[N.fetch]);
  expect(second.children[N.docs]).toBe(first.children[N.docs]);
  expect(Object.values(first.children)).not.toContain(second.children[N.index]);
  expect((await revisionChildren(first.id)).map((i) => i.childId).sort()).toEqual(Object.values(first.children).sort());

  // History pages follow publication order; the seeded capability already holds earlier suites' revisions.
  await carol.goto(`${pagePath}/children/${first.children[N.summarize]}?after=${first.sequence - 1}`);
  const history = carol.getByTestId("history-row");
  const presence = await history.getByTestId("history-presence").allTextContents();
  const versions = await history.getByTestId("history-version").allTextContents();
  expect(presence[versions.indexOf(V1)]).toBe("Present");
  expect(presence[versions.indexOf(V2)]).toBe("Absent");
  await expectAccessible(carol);
  await shot(carol, "04-child-present-absent.png");

  // MCP validation already refuses two tools with one name, so an a2a card repeats a skill id: validate 200, publish 422.
  const body = a2aDuplicate(`2.2.0-dup.r${run}`);
  const before = count(`SELECT count(*) FROM gozleme.manifest_revisions WHERE capability_id = '${CAPABILITY.id}'`);
  const validated = await api(carol, "POST", VALIDATE, body);
  expect([validated.status, validated.json().outcome]).toEqual([200, "accepted"]);
  const refused = await publish(carol, body);
  expect(refused.status).toBe(422);
  const problems = refused.json().problems as { rule: string; location: { part: string; pointer: string } }[];
  expect(problems.map((p) => [p.rule, p.location.part])).toEqual([
    ["publish.duplicate_entry", "projection"],
    ["publish.duplicate_entry", "projection"],
  ]);
  expect(count(`SELECT count(*) FROM gozleme.manifest_revisions WHERE capability_id = '${CAPABILITY.id}'`)).toBe(before);

  await freshMinute(carol);
  await publishInBrowser(body);
  await expect(carol.getByTestId("publish-error")).toBeVisible();
  await expect(carol.getByTestId("publish-revision").getByTestId("problem-rule")).toHaveText(["publish.duplicate_entry", "publish.duplicate_entry"]);
  await expect(carol.getByTestId("publish-revision").getByTestId("problem-location")).toHaveText([/^projection \/entries\/\d+$/, /^projection \/entries\/\d+$/]);
  await carol.getByTestId("publish-revision").scrollIntoViewIfNeeded();
  await shot(carol, "05-duplicate-entry-422.png");
  expect(count(`SELECT count(*) FROM gozleme.manifest_revisions WHERE capability_id = '${CAPABILITY.id}'`)).toBe(before);
  Object.assign(record, { continuity: { revision: second.id, kept: [second.children[N.search], second.children[N.fetch], second.children[N.docs]], added: second.children[N.index], absent: first.children[N.summarize] }, duplicate: { validate: 200, publish: 422, pointers: problems.map((p) => p.location.pointer), stored: 0 } });
});

test("3. projection-regenerate adds an unattested generation and IDs never stand in for each other", async () => {
  test.setTimeout(240_000);
  const regenerated = projectionRegenerate(first.id);
  expect(regenerated.exit, regenerated.stderr).toBe(0);
  // The JSON console log shares stdout and is written asynchronously, so the result line is found, not assumed last.
  const result = new RegExp(`^${first.id} generation 2 (${UUID.source}) digest equals generation 1: yes$`);
  const line = regenerated.stdout.split("\n").map((l) => result.exec(l)).find((m) => m !== null) ?? null;
  expect(line, regenerated.stdout).not.toBeNull();
  generation2 = line![1]!;
  const unknown = projectionRegenerate("0193a000-0000-7000-8000-00000000ffff");
  expect([unknown.exit, unknown.stderr.split("\n").includes("unknown_target")]).toEqual([2, true]);
  expect(projectionRegenerate("not-a-uuid").exit).toBe(64);

  await carol.goto(`${pagePath}/revisions/${first.id}`);
  const rows = carol.getByTestId("generation-row");
  await expect(rows).toHaveCount(2);
  await expect(rows.getByTestId("generation-attested")).toHaveText(["Attested at publication", "Not attested"]);
  await expect(rows.nth(1).getByTestId("generation-id")).toHaveText(generation2);
  const digests = await rows.getByTestId("generation-digest").evaluateAll((els) => els.map((e) => e.getAttribute("data-value")));
  expect(digests[0]).toBe(digests[1]);
  await expect(carol.getByTestId("verification-badge")).toHaveAttribute("data-status", "verified");
  expect(byName((await revisionChildren(first.id)) as any)).toEqual(first.children);
  await carol.getByTestId("projection-generations").scrollIntoViewIfNeeded();
  await expectAccessible(carol);
  await shot(carol, "06-projection-generations.png");

  const generation1 = (await api(carol, "GET", `${REVISIONS}/${first.id}/projections`)).json().items[0].id as string;
  const swapped: Record<string, number> = {};
  for (const url of [
    `${CHILDREN}/${generation2}`,
    `${CHILDREN}/${first.id}`,
    `${REVISIONS}/${first.id}/projections/${first.children[N.search]}`,
    `${REVISIONS}/${first.id}/projections/${first.id}`,
    `${REVISIONS}/${second.id}/projections/${generation1}`,
    `${REVISIONS}/${first.children[N.search]}/children`,
  ]) {
    const res = await api(carol, "GET", url);
    expect([res.status, res.text], url).toEqual([404, ""]);
    swapped[url.replace(ORG_API, "")] = res.status;
  }
  const download = await carol.request.get(`${REVISIONS}/${first.id}/projections/${generation2}`, { headers: { "x-gozleme-client": "web" } });
  expect(download.headers()["content-disposition"]).toContain("projection-generation-2.json");
  Object.assign(record, { regenerate: { revision: first.id, generation: generation2, exit: 0, identical: true, unknownExit: 2, usageExit: 64 }, swappedIds: swapped });
});

test("4. children are immutable and private", async ({ browser }) => {
  test.setTimeout(240_000);
  const sql: Record<string, string> = {};
  const where: Record<string, string> = {
    capability_children: `capability_id = '${CAPABILITY.id}'`,
    revision_children: `revision_id = '${first.id}'`,
    projection_generations: `revision_id = '${first.id}'`,
  };
  const set: Record<string, string> = { capability_children: "native_name = native_name", revision_children: "title = title", projection_generations: "generation = generation" };
  // Forced RLS has no UPDATE or DELETE policy on these tables, so for every login with RLS (Carol's context, or the
  // operator: owner, no context) UPDATE/DELETE reach no row; TRUNCATE is not row-filtered and reaches the guard. The
  // superuser bypasses RLS, so its UPDATE/DELETE reach the immutability guard itself and its SQLSTATE is captured.
  for (const [role, account, label] of [
    ["gozleme_app", carolId, "gozleme_app"],
    ["gozleme_migrator", carolId, "gozleme_migrator"],
    ["gozleme_migrator", null, "gozleme_migrator operator"],
    ["postgres", null, "superuser"],
  ] as const) {
    for (const table of TABLES) {
      for (const [op, statement] of [
        ["update", `UPDATE gozleme.${table} SET ${set[table]} WHERE ${where[table]}`],
        ["delete", `DELETE FROM gozleme.${table} WHERE ${where[table]}`],
      ] as const) {
        const outcome = attempt(role, account, statement);
        expect(role === "postgres" ? ["42501", "P0001"] : ["0 row(s)", "42501", "P0001"], `${label} ${op} ${table}`).toContain(outcome);
        sql[`${label} ${op} ${table}`] = outcome;
      }
      const truncate = execute(role, account, `TRUNCATE gozleme.${table} CASCADE`);
      expect(truncate, `${label} truncate ${table}`).not.toBe("ok");
      sql[`${label} truncate ${table}`] = truncate;
    }
  }

  const reads = [CHILDREN, `${CHILDREN}/${first.children[N.search]}`, `${REVISIONS}/${first.id}/children`, `${REVISIONS}/${first.id}/projections`, `${REVISIONS}/${first.id}/projections/${generation2}`];
  for (const url of reads) {
    const r = await api(dave, "GET", url);
    expect([r.status, r.text], url).toEqual([404, ""]);
  }

  const anonymous = await browser.newContext();
  const visitor = await anonymous.newPage();
  await visitor.goto(`${pagePath}/children`);
  await visitor.waitForURL(/\/login\?returnTo=/);
  const signedOut = await visitor.request.get(CHILDREN, { headers: { "x-gozleme-client": "web" } });
  expect(signedOut.status()).toBe(401);
  await anonymous.close();

  const methods: Record<string, number> = {};
  for (const method of ["DELETE", "PUT", "PATCH", "POST"]) {
    methods[method] = (await api(carol, method, `${CHILDREN}/${first.children[N.search]}`, "{}")).status;
    expect(methods[method], method).toBe(405);
  }
  Object.assign(record, { immutability: { sql, methods }, privacy: { nonMember: 404, signedOutApi: 401, signedOutPage: "redirect to sign in" } });
  expect(PARTNER_ORG.id).not.toBe(DEMO_ORG.id);
});

test("5. every earlier revision has generation 1 and its children, and still verifies", async () => {
  test.setTimeout(240_000);
  const revisions = count("SELECT count(*) FROM gozleme.manifest_revisions");
  expect(count("SELECT count(*) FROM gozleme.manifest_revisions r WHERE NOT EXISTS (SELECT 1 FROM gozleme.projection_generations g WHERE g.revision_id = r.id AND g.generation = 1)")).toBe(0);
  expect(
    count(
      "SELECT count(*) FROM gozleme.manifest_revisions r WHERE (SELECT count(*) FROM gozleme.revision_children rc WHERE rc.revision_id = r.id) <> jsonb_array_length(convert_from(r.projection, 'UTF8')::jsonb -> 'entries')",
    ),
  ).toBe(0);

  // The GOZ-0007 suite's revisions of the seeded capability verify as before (its deliberately tampered ones excepted).
  const list = (await api(carol, "GET", `${REVISIONS}?limit=100`)).json().revisions as { id: string; version: string; verification: { status: string } }[];
  const earlier = list.filter((r) => !r.version.includes("tamper") && !r.version.endsWith(`r${run}`));
  expect(earlier.length).toBeGreaterThan(0);
  for (const r of earlier) expect(r.verification.status, r.version).toBe("verified");
  Object.assign(record, { install: { revisions, withoutGeneration1: 0, withMismatchedChildCount: 0, earlierVerified: earlier.length } });
});

test("6. Jaeger shows the assign, generation and operator spans, and no name or digest reaches telemetry", async ({ request }) => {
  test.setTimeout(240_000);
  let assignTrace: string | undefined;
  let traces: Awaited<ReturnType<typeof recentSpans>> = [];
  await expect
    .poll(
      async () => {
        traces = await recentSpans("gozleme-api", 20, "gozleme.children.assign");
        // The second revision's publication: found by its revision ID, since the seeded capability keeps every run's children.
        const publish = traces.find((s) => s.name === "gozleme.manifest.publish" && s.attributes["gozleme.revision.id"] === second.id);
        assignTrace = publish && traces.some((s) => s.traceId === publish.traceId && s.name === "gozleme.children.assign") ? publish.traceId : undefined;
        return assignTrace !== undefined;
      },
      { timeout: 90_000, intervals: [3000] },
    )
    .toBe(true);
  const assign = traces.find((s) => s.traceId === assignTrace && s.name === "gozleme.children.assign")!;
  expect(Object.keys(assign.attributes).filter((k) => k.startsWith("gozleme.")).sort()).toEqual(
    ["gozleme.children.absent", "gozleme.children.created", "gozleme.children.declared", "gozleme.children.matched", "gozleme.manifest.family"].sort(),
  );
  expect([assign.attributes["gozleme.children.declared"], assign.attributes["gozleme.children.created"], assign.attributes["gozleme.children.matched"]].map(Number)).toEqual([4, 1, 3]);
  // Absent counts every earlier child of the capability this revision does not declare: this run's prompt and earlier runs' children.
  const absent = Number(assign.attributes["gozleme.children.absent"]);
  expect(absent).toBeGreaterThanOrEqual(1);
  expect(traces.some((s) => s.traceId === assignTrace && s.name === "gozleme.projection.generation.record")).toBe(true);

  let operatorSpans: Awaited<ReturnType<typeof recentSpans>> = [];
  await expect
    .poll(async () => {
      operatorSpans = (await recentSpans("gozleme-operator", 20, "gozleme.operator.projection_regenerate")).filter((s) => s.name === "gozleme.operator.projection_regenerate");
      return operatorSpans.some((s) => Number(s.attributes["gozleme.operator.exit_code"]) === 0);
    }, { timeout: 60_000, intervals: [3000] })
    .toBe(true);
  for (const span of operatorSpans) expect(Object.keys(span.attributes).filter((k) => k.startsWith("gozleme."))).toEqual(["gozleme.operator.exit_code"]);

  let metrics = "";
  await expect
    .poll(async () => {
      metrics = await (await request.get(METRICS_URL)).text();
      return metrics.includes("gozleme_children_assignments_total{") && metrics.includes("gozleme_projection_generations_total{");
    }, { timeout: 90_000, intervals: [5000] })
    .toBe(true);
  const lines = metrics.split("\n").filter((l) => /^gozleme_(children_assignments|projection_generations)_total\{/.test(l));
  for (const line of lines) {
    const labels = [...line.matchAll(/(\w+)="/g)].map((m) => m[1]!).filter((k) => k.startsWith("gozleme_")).sort();
    expect(labels).toEqual(line.startsWith("gozleme_children") ? ["gozleme_child_kind", "gozleme_children_outcome", "gozleme_manifest_family"] : ["gozleme_manifest_family", "gozleme_projection_outcome"]);
  }

  const digests: string[] = [];
  for (const revision of [first.id, second.id]) {
    digests.push(...((await api(carol, "GET", `${REVISIONS}/${revision}/projections`)).json().items as { digest: string }[]).map((g) => g.digest.slice("sha256:".length)));
  }
  const privateValues = [...Object.values(N), ...Object.values(T), ...digests, `Repeated skill ${run}`, V1, V2, DEMO.carol, DEMO.dave, DEMO_PASSWORD, ...secretStrings];
  const shotTrace = async (traceId: string, span: string, file: string) => {
    const page = await alice.context().newPage();
    await page.setViewportSize({ width: 1600, height: 1000 });
    await page.goto(`${JAEGER_URL}/trace/${traceId}`);
    await expect(page.getByText(span).first()).toBeAttached({ timeout: 15_000 });
    await page.getByRole("switch", { name: new RegExp(`^gozleme-[\\w-]+ ${span.replaceAll(".", "\\.")}`) }).first().click();
    await expect(page.getByRole("heading", { name: span, level: 2 })).toBeVisible();
    await page.getByText(/^Attributes:?$/).first().click();
    for (const v of privateValues) await expect(page.locator("body")).not.toContainText(v);
    await page.screenshot({ path: `${shots}/${file}`, fullPage: true });
    await page.close();
  };
  await shotTrace(assignTrace!, "gozleme.children.assign", "70-children-assign-trace-jaeger.png");
  await shotTrace(assignTrace!, "gozleme.projection.generation.record", "71-generation-record-trace-jaeger.png");
  const succeeded = operatorSpans.find((s) => Number(s.attributes["gozleme.operator.exit_code"]) === 0)!;
  await shotTrace(succeeded.traceId, "gozleme.operator.projection_regenerate", "72-projection-regenerate-trace-jaeger.png");

  // SC-007: no run-unique native name, title or digest in spans, metrics, stored logs or container logs.
  const spans = [...(await recentSpans("gozleme-api", 20)), ...(await recentSpans("gozleme-web", 20)), ...operatorSpans];
  const logs = await storedLogs('{service_name=~"gozleme-.+"}', 20);
  const telemetry = JSON.stringify(spans) + lines.join("\n") + JSON.stringify(logs.filter((l) => l.at >= suiteStarted));
  for (const v of privateValues) expect(telemetry, "telemetry must not contain a private value").not.toContain(v);
  const composeLogs = execFileSync("docker", ["compose", "logs", "--no-color", "--since", new Date(suiteStarted).toISOString()], { cwd: repoRoot, maxBuffer: 64 * 1024 * 1024 }).toString();
  for (const v of privateValues) expect(composeLogs, "compose logs must not contain a private value").not.toContain(v);

  writeEvidence({
    ...record,
    observability: {
      spans: ["gozleme.children.assign", "gozleme.projection.generation.record", "gozleme.operator.projection_regenerate"],
      assignCounts: { declared: 4, created: 1, matched: 3, absent },
      counters: ["gozleme_children_assignments_total", "gozleme_projection_generations_total"],
      noNamesInSpansMetricsOrLogs: true,
    },
  });
});

/** Merges into `children-evidence.json` (the SC-006 timing from the integration suite stays). Opaque IDs only. */
function writeEvidence(fields: Record<string, unknown>) {
  const file = `${evidence}/children-evidence.json`;
  const existing = existsSync(file) ? (JSON.parse(readFileSync(file, "utf8")) as Record<string, unknown>) : {};
  const merged = {
    ...existing,
    spec: "GOZ-0008",
    capturedAt: new Date().toISOString(),
    runId: run,
    origin: ORIGIN,
    organization: DEMO_ORG.id,
    publisher: PUBLISHER.id,
    capability: { id: CAPABILITY.id, canonicalId: CANONICAL },
    demo: fields,
  };
  const text = JSON.stringify(merged, null, 2);
  for (const v of [DEMO.alice, DEMO.carol, DEMO.dave, DEMO_ORG.name, PARTNER_ORG.name, DEMO_PASSWORD, ...secretStrings, ...Object.values(N), ...Object.values(T)]) expect(text).not.toContain(v);
  writeFileSync(file, `${text}\n`);
}
