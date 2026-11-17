import { afterEach, describe, expect, it, vi } from "vitest";
import { PATCH as renameCapability } from "./[publisherId]/capabilities/[capabilityId]/route";
import { POST as createCapability } from "./[publisherId]/capabilities/route";
import { PATCH as renamePublisher } from "./[publisherId]/route";
import { POST as register } from "./route";

const ORG = "0193a000-0000-7000-8000-000000000002";
const PUB = "0193a000-0000-7000-8000-000000000005";
const CAP = "0193a000-0000-7000-8000-000000000009";

const request = (method: string, headers: Record<string, string> = {}) =>
  new Request("http://localhost:3000/api/organizations/x/publishers", {
    method,
    body: '{"handle":"Acme Tools","displayName":"A"}',
    headers: { "content-type": "application/json", ...headers },
  });

type Handler = (req: Request, ctx: never) => Promise<Response>;
const ctx = (p: Record<string, string>) => ({ params: Promise.resolve(p) });
const unsafe: [string, string, Handler, unknown, RegExp][] = [
  ["POST …/publishers", "POST", register as Handler, ctx({ organizationId: ORG }), new RegExp(`/api/v1/organizations/${ORG}/publishers$`)],
  ["PATCH …/publishers/{id}", "PATCH", renamePublisher as Handler, ctx({ organizationId: ORG, publisherId: PUB }), new RegExp(`/publishers/${PUB}$`)],
  ["POST …/capabilities", "POST", createCapability as Handler, ctx({ organizationId: ORG, publisherId: PUB }), new RegExp(`/publishers/${PUB}/capabilities$`)],
  [
    "PATCH …/capabilities/{id}",
    "PATCH",
    renameCapability as Handler,
    ctx({ organizationId: ORG, publisherId: PUB, capabilityId: CAP }),
    new RegExp(`/publishers/${PUB}/capabilities/${CAP}$`),
  ],
];

describe("publisher route handlers (CSRF layer 1, GOZ-0005)", () => {
  afterEach(() => vi.restoreAllMocks());

  it.each(unsafe)("%s refuses cross-site requests without calling the API", async (_name, method, handler, context) => {
    const upstream = vi.spyOn(globalThis, "fetch");
    const res = await handler(request(method, { "sec-fetch-site": "cross-site" }), context as never);
    expect(res.status).toBe(403);
    expect(upstream).not.toHaveBeenCalled();
  });

  it.each(unsafe)("%s refuses requests with neither fetch metadata nor origin", async (_name, method, handler, context) => {
    const upstream = vi.spyOn(globalThis, "fetch");
    const res = await handler(request(method), context as never);
    expect(res.status).toBe(403);
    expect(upstream).not.toHaveBeenCalled();
  });

  it.each(unsafe)("same-origin %s forwards the body unchanged and relays the answer", async (_name, method, handler, context, path) => {
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response('{"error":"handle_unavailable"}', { status: 409 }));
    const res = await handler(request(method, { "sec-fetch-site": "same-origin" }), context as never);
    expect(res.status).toBe(409);
    expect(await res.text()).toBe('{"error":"handle_unavailable"}');
    const [url, init] = upstream.mock.calls[0]! as [URL, RequestInit];
    expect(String(url)).toMatch(path);
    expect(init.method).toBe(method);
    // Never normalized by the web tier: the API decides (US4).
    expect(init.body).toBe('{"handle":"Acme Tools","displayName":"A"}');
  });

  it("relays the rate limit status", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response('{"error":"rate_limited"}', { status: 429 }));
    const res = await (register as Handler)(request("POST", { "sec-fetch-site": "same-origin" }), ctx({ organizationId: ORG }) as never);
    expect(res.status).toBe(429);
  });

  it.each([".", ".."])("never lets the dot segment %s retarget the API call", async (id) => {
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 404 }));
    await (renameCapability as Handler)(request("PATCH", { "sec-fetch-site": "same-origin" }), ctx({ organizationId: ORG, publisherId: id, capabilityId: id }) as never);
    expect(String(upstream.mock.calls[0]![0])).toMatch(new RegExp(`/api/v1/organizations/${ORG}/publishers/not-found/capabilities/not-found$`));
  });
});
