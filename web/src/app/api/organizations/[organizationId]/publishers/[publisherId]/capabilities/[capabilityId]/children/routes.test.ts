import { afterEach, describe, expect, it, vi } from "vitest";
import { GET as generation } from "../revisions/[revisionId]/projections/[generationId]/route";
import { GET as generations } from "../revisions/[revisionId]/projections/route";
import { GET as revisionChildren } from "../revisions/[revisionId]/children/route";
import { GET as child } from "./[childId]/route";
import { GET as list } from "./route";

const ORG = "0193a000-0000-7000-8000-000000000002";
const PUB = "0193a000-0000-7000-8000-000000000010";
const CAP = "0193a000-0000-7000-8000-000000000011";
const REV = "0193a000-0000-7000-8000-000000000020";
const CHILD = "0193a000-0000-7000-8000-000000000050";
const GEN = "0193a000-0000-7000-8000-000000000060";
const API = `/api/v1/organizations/${ORG}/publishers/${PUB}/capabilities/${CAP}`;
const get = (url = "http://localhost:3000/api/x") => new Request(url, { headers: { cookie: "__Host-gozleme=s" } });
const ids = { organizationId: ORG, publisherId: PUB, capabilityId: CAP };

describe("child and projection route handlers (GOZ-0008)", () => {
  afterEach(() => vi.restoreAllMocks());

  it("forwards the five reads as the web client with the session cookie, passing only integer after and limit", async () => {
    const upstream = vi.spyOn(globalThis, "fetch").mockImplementation(async () => Response.json({ items: [] }));
    await list(get("http://localhost:3000/api/x?after=4&limit=10&x=1"), { params: Promise.resolve(ids) });
    await child(get("http://localhost:3000/api/x?after=../x"), { params: Promise.resolve({ ...ids, childId: CHILD }) });
    await revisionChildren(get("http://localhost:3000/api/x?limit=100"), { params: Promise.resolve({ ...ids, revisionId: REV }) });
    await generations(get(), { params: Promise.resolve({ ...ids, revisionId: REV }) });
    const urls = upstream.mock.calls.map((c) => String(c[0]));
    expect(urls[0]).toMatch(new RegExp(`${API}/children\\?after=4&limit=10$`));
    expect(urls[1]).toMatch(new RegExp(`${API}/children/${CHILD}$`));
    expect(urls[2]).toMatch(new RegExp(`${API}/revisions/${REV}/children\\?limit=100$`));
    expect(urls[3]).toMatch(new RegExp(`${API}/revisions/${REV}/projections$`));
    for (const [, init] of upstream.mock.calls as [URL, RequestInit][]) {
      expect((init.headers as Headers).get("x-gozleme-client")).toBe("web");
      expect((init.headers as Headers).get("cookie")).toBe("__Host-gozleme=s");
    }
  });

  it("relays a generation download's bytes and headers unchanged", async () => {
    const bytes = new TextEncoder().encode('{"entries":[],"projectionVersion":"1"}');
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(bytes, {
        status: 200,
        headers: { "content-type": "application/json", "content-disposition": 'attachment; filename="projection-generation-2.json"', "x-content-type-options": "nosniff" },
      }),
    );
    const res = await generation(get(), { params: Promise.resolve({ ...ids, revisionId: REV, generationId: GEN }) });
    expect(String(upstream.mock.calls[0]![0])).toMatch(new RegExp(`${API}/revisions/${REV}/projections/${GEN}$`));
    expect(Array.from(new Uint8Array(await res.arrayBuffer()))).toEqual(Array.from(bytes));
    expect(res.headers.get("content-disposition")).toBe('attachment; filename="projection-generation-2.json"');
    expect(res.headers.get("x-content-type-options")).toBe("nosniff");
  });

  it("passes an empty 404 through and maps upstream failures to 503", async () => {
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(new Response(null, { status: 404 }));
    const res = await child(get(), { params: Promise.resolve({ ...ids, childId: GEN }) });
    expect(res.status).toBe(404);
    expect(await res.text()).toBe("");
    upstream.mockRejectedValueOnce(new TypeError("down"));
    expect((await list(get(), { params: Promise.resolve(ids) })).status).toBe(503);
  });

  it("never lets a dot segment retarget the API call", async () => {
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 404 }));
    await generation(get(), { params: Promise.resolve({ organizationId: ORG, publisherId: PUB, capabilityId: CAP, revisionId: "..", generationId: "." }) });
    expect(String(upstream.mock.calls[0]![0])).toMatch(/\/revisions\/not-found\/projections\/not-found$/);
  });

  it("exports no write handler", async () => {
    for (const mod of [await import("./route"), await import("./[childId]/route"), await import("../revisions/[revisionId]/children/route"), await import("../revisions/[revisionId]/projections/route"), await import("../revisions/[revisionId]/projections/[generationId]/route")]) {
      expect(Object.keys(mod).filter((k) => ["POST", "PUT", "PATCH", "DELETE"].includes(k))).toEqual([]);
    }
  });
});
