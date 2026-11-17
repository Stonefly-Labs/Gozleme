import { afterEach, describe, expect, it, vi } from "vitest";
import { POST } from "./route";

const ORG = "0193a000-0000-7000-8000-000000000002";
const PUB = "0193a000-0000-7000-8000-000000000010";
const CAP = "0193a000-0000-7000-8000-000000000011";
const BODY = '{"envelopeVersion":1,"native":{"document":"  {\\"a\\" : 1}\\n"}}';
const ctx = { params: Promise.resolve({ organizationId: ORG, publisherId: PUB, capabilityId: CAP }) };

const request = (headers: Record<string, string> = {}) =>
  new Request("http://localhost:3000/api/x", { method: "POST", body: BODY, headers: { "content-type": "application/json", ...headers } });

describe("manifest validate route handler (GOZ-0006)", () => {
  afterEach(() => vi.restoreAllMocks());

  it("refuses cross-site requests without calling the API", async () => {
    const upstream = vi.spyOn(globalThis, "fetch");
    expect((await POST(request({ "sec-fetch-site": "cross-site" }), ctx)).status).toBe(403);
    expect((await POST(request(), ctx)).status).toBe(403);
    expect(upstream).not.toHaveBeenCalled();
  });

  it.each([
    [200, '{"outcome":"rejected","problems":[],"truncated":false}'],
    [403, '{"error":"forbidden"}'],
    [404, ""],
    [409, '{"error":"suspended"}'],
    [429, '{"error":"rate_limited"}'],
    [503, '{"error":"unavailable"}'],
  ])("passes %i and its body through, forwarding the body unchanged as the web client", async (status, body) => {
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(body || null, { status, headers: { "content-type": "application/json" } }));
    const res = await POST(request({ "sec-fetch-site": "same-origin", cookie: "__Host-gozleme=s" }), ctx);
    expect(res.status).toBe(status);
    expect(await res.text()).toBe(body);
    const [url, init] = upstream.mock.calls[0]! as [URL, RequestInit];
    expect(String(url)).toMatch(new RegExp(`/api/v1/organizations/${ORG}/publishers/${PUB}/capabilities/${CAP}/manifests/validate$`));
    expect(init.method).toBe("POST");
    expect(init.body).toBe(BODY);
    const headers = init.headers as Headers;
    expect(headers.get("x-gozleme-client")).toBe("web");
    expect(headers.get("cookie")).toBe("__Host-gozleme=s");
  });

  it("never lets a dot segment retarget the API call", async () => {
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 404 }));
    await POST(request({ "sec-fetch-site": "same-origin" }), { params: Promise.resolve({ organizationId: ORG, publisherId: "..", capabilityId: "." }) });
    expect(String(upstream.mock.calls[0]![0])).toMatch(/\/publishers\/not-found\/capabilities\/not-found\/manifests\/validate$/);
  });
});
