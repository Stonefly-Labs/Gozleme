import { afterEach, describe, expect, it, vi } from "vitest";

const incoming = new Headers({ cookie: "gozleme_session=abc" });
vi.mock("next/headers", () => ({ headers: async () => incoming }));

const { getOrganization, listMembers, listOrganizations, segment } = await import("./organizations");

const respond = (status: number, body?: unknown) =>
  vi.fn(async () => new Response(body === undefined ? null : JSON.stringify(body), { status })) as unknown as typeof fetch & ReturnType<typeof vi.fn>;

describe("organization page data (session-forwarding reads)", () => {
  afterEach(() => vi.unstubAllEnvs());

  it("forwards the session cookie and the web-client header and returns the data", async () => {
    vi.stubEnv("GOZLEME_API_URL", "http://api:8080");
    const fetchImpl = respond(200, { organizations: [], activeOrganizationId: null });
    expect(await listOrganizations(fetchImpl)).toEqual({ state: "ok", data: { organizations: [], activeOrganizationId: null } });
    const [url, init] = fetchImpl.mock.calls[0]! as unknown as [URL, RequestInit];
    expect(new URL(String(url)).pathname).toBe("/api/v1/organizations");
    expect(new URL(String(url)).host).toBe("api:8080");
    expect((init.headers as Headers).get("cookie")).toBe("gozleme_session=abc");
    expect((init.headers as Headers).get("x-gozleme-client")).toBe("web");
  });

  it.each([
    [401, "anonymous"],
    [404, "not_found"],
    [503, "unavailable"],
    [500, "unavailable"],
  ])("maps %i to %s", async (status, state) => {
    expect(await getOrganization("x", respond(status))).toEqual({ state });
  });

  it("treats network failures as unavailable, never as data", async () => {
    const failing = vi.fn(async () => Promise.reject(new Error("down"))) as unknown as typeof fetch;
    expect(await listMembers("x", failing)).toEqual({ state: "unavailable" });
  });

  it("encodes ids and neutralises dot segments", () => {
    expect(segment("a/b?c")).toBe("a%2Fb%3Fc");
    expect(segment("..")).toBe("not-found");
    expect(segment("0193a000-0000-7000-8000-000000000002")).toBe("0193a000-0000-7000-8000-000000000002");
  });
});
