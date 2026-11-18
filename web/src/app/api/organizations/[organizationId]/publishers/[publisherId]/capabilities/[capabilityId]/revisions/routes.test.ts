import { afterEach, describe, expect, it, vi } from "vitest";
import { GET as part } from "./[revisionId]/[part]/route";
import { GET as read } from "./[revisionId]/route";
import { GET as publicKey } from "../../../signing-keys/[keyId]/public-key/route";
import { GET as keys } from "../../../signing-keys/route";
import { GET as list, POST } from "./route";

const ORG = "0193a000-0000-7000-8000-000000000002";
const PUB = "0193a000-0000-7000-8000-000000000010";
const CAP = "0193a000-0000-7000-8000-000000000011";
const REV = "0193a000-0000-7000-8000-000000000020";
const KEY = "0193a000-0000-7000-8000-000000000030";
const BODY = '{"envelopeVersion":1,"native":{"document":"  {\\"a\\" : 1}\\n"}}';
const ctx = { params: Promise.resolve({ organizationId: ORG, publisherId: PUB, capabilityId: CAP }) };
const API = `/api/v1/organizations/${ORG}/publishers/${PUB}`;

const post = (headers: Record<string, string> = {}) =>
  new Request("http://localhost:3000/api/x", { method: "POST", body: BODY, headers: { "content-type": "application/json", ...headers } });
const get = (url = "http://localhost:3000/api/x") => new Request(url, { headers: { cookie: "__Host-gozleme=s" } });

describe("revision route handlers (GOZ-0007)", () => {
  afterEach(() => vi.restoreAllMocks());

  it("refuses cross-site publications without calling the API", async () => {
    const upstream = vi.spyOn(globalThis, "fetch");
    expect((await POST(post({ "sec-fetch-site": "cross-site" }), ctx)).status).toBe(403);
    expect((await POST(post(), ctx)).status).toBe(403);
    expect(upstream).not.toHaveBeenCalled();
  });

  it.each([
    [201, '{"id":"r"}'],
    [422, '{"outcome":"rejected","problems":[],"truncated":false}'],
    [403, '{"error":"forbidden"}'],
    [404, ""],
    [409, '{"error":"version_exists"}'],
    [409, '{"error":"suspended"}'],
    [429, '{"error":"rate_limited"}'],
    [503, '{"error":"unavailable"}'],
  ])("passes a publication's %i and body through, forwarding the body unchanged as the web client", async (status, body) => {
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(body || null, { status, headers: { "content-type": "application/json" } }));
    const res = await POST(post({ "sec-fetch-site": "same-origin", cookie: "__Host-gozleme=s" }), ctx);
    expect(res.status).toBe(status);
    expect(await res.text()).toBe(body);
    const [url, init] = upstream.mock.calls[0]! as [URL, RequestInit];
    expect(String(url)).toMatch(new RegExp(`${API}/capabilities/${CAP}/revisions$`));
    expect(init.method).toBe("POST");
    expect(init.body).toBe(BODY);
    expect((init.headers as Headers).get("x-gozleme-client")).toBe("web");
    expect((init.headers as Headers).get("cookie")).toBe("__Host-gozleme=s");
  });

  it("forwards only integer after and limit when listing", async () => {
    const upstream = vi.spyOn(globalThis, "fetch").mockImplementation(async () => Response.json({ revisions: [] }));
    await list(get("http://localhost:3000/api/x?after=50&limit=10&x=1"), ctx);
    await list(get("http://localhost:3000/api/x?after=../x&limit=-1"), ctx);
    expect(String(upstream.mock.calls[0]![0])).toMatch(new RegExp(`${API}/capabilities/${CAP}/revisions\\?after=50&limit=10$`));
    expect(String(upstream.mock.calls[1]![0])).toMatch(new RegExp(`${API}/capabilities/${CAP}/revisions$`));
  });

  it("forwards the detail and the key list", async () => {
    const upstream = vi.spyOn(globalThis, "fetch").mockImplementation(async () => Response.json({}));
    await read(get(), { params: Promise.resolve({ organizationId: ORG, publisherId: PUB, capabilityId: CAP, revisionId: REV }) });
    await keys(get(), { params: Promise.resolve({ organizationId: ORG, publisherId: PUB }) });
    expect(String(upstream.mock.calls[0]![0])).toMatch(new RegExp(`${API}/capabilities/${CAP}/revisions/${REV}$`));
    expect(String(upstream.mock.calls[1]![0])).toMatch(new RegExp(`${API}/signing-keys$`));
  });

  it("relays download bytes and headers unchanged", async () => {
    const signature = new Uint8Array(64).map((_, i) => (i * 37 + 200) % 256);
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(signature, {
        status: 200,
        headers: { "content-type": "application/octet-stream", "content-disposition": 'attachment; filename="statement.sig"', "x-content-type-options": "nosniff" },
      }),
    );
    const res = await part(get(), { params: Promise.resolve({ organizationId: ORG, publisherId: PUB, capabilityId: CAP, revisionId: REV, part: "signature" }) });
    expect(String(upstream.mock.calls[0]![0])).toMatch(new RegExp(`${API}/capabilities/${CAP}/revisions/${REV}/signature$`));
    expect(res.status).toBe(200);
    expect(new Uint8Array(await res.arrayBuffer())).toEqual(signature);
    expect(res.headers.get("content-type")).toBe("application/octet-stream");
    expect(res.headers.get("content-disposition")).toBe('attachment; filename="statement.sig"');
    expect(res.headers.get("x-content-type-options")).toBe("nosniff");
    expect(res.headers.get("cache-control")).toBe("no-store");
  });

  it.each(["envelope", "public-key", "..", "Native"])("answers an unknown part %s with 404 without calling the API", async (name) => {
    const upstream = vi.spyOn(globalThis, "fetch");
    const res = await part(get(), { params: Promise.resolve({ organizationId: ORG, publisherId: PUB, capabilityId: CAP, revisionId: REV, part: name }) });
    expect(res.status).toBe(404);
    expect(upstream).not.toHaveBeenCalled();
  });

  it("forwards the public key and maps upstream failures to 503", async () => {
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(
      new Response("-----BEGIN PUBLIC KEY-----\n", { headers: { "content-type": "application/x-pem-file", "content-disposition": 'attachment; filename="public-key.pem"' } }),
    );
    const res = await publicKey(get(), { params: Promise.resolve({ organizationId: ORG, publisherId: PUB, keyId: KEY }) });
    expect(String(upstream.mock.calls[0]![0])).toMatch(new RegExp(`${API}/signing-keys/${KEY}/public-key$`));
    expect(res.headers.get("content-type")).toBe("application/x-pem-file");
    upstream.mockRejectedValueOnce(new TypeError("down"));
    expect((await publicKey(get(), { params: Promise.resolve({ organizationId: ORG, publisherId: PUB, keyId: KEY }) })).status).toBe(503);
  });

  it("never lets a dot segment retarget the API call", async () => {
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 404 }));
    await part(get(), { params: Promise.resolve({ organizationId: ORG, publisherId: "..", capabilityId: ".", revisionId: "..", part: "native" }) });
    expect(String(upstream.mock.calls[0]![0])).toMatch(/\/publishers\/not-found\/capabilities\/not-found\/revisions\/not-found\/native$/);
  });
});
