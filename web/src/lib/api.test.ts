import { afterEach, describe, expect, it, vi } from "vitest";
import { forwardToApi } from "./api";

const incoming = (init: RequestInit & { headers?: Record<string, string> } = {}) =>
  new Request("http://web:3000/api/auth/sign-in", { method: "POST", body: '{"email":"a@b.test"}', ...init });

describe("forwardToApi (research R4)", () => {
  afterEach(() => vi.unstubAllEnvs());

  it("forwards body, cookie, client address and the web-client header; relays set-cookie", async () => {
    vi.stubEnv("GOZLEME_API_URL", "http://api:8080");
    const upstreamHeaders = new Headers({ "content-type": "application/json" });
    upstreamHeaders.append("set-cookie", "gozleme_session=abc; path=/; httponly; samesite=lax");
    const fetchImpl = vi.fn(async () => new Response('{"userId":"x"}', { status: 200, headers: upstreamHeaders }));
    const res = await forwardToApi(
      incoming({ headers: { cookie: "gozleme_session=old; other=1", "x-forwarded-for": "203.0.113.5", authorization: "Bearer nope" } }),
      "/api/v1/auth/sign-in",
      { fetchImpl: fetchImpl as unknown as typeof fetch },
    );

    const [url, init] = fetchImpl.mock.calls[0]! as unknown as [URL, RequestInit];
    expect(String(url)).toBe("http://api:8080/api/v1/auth/sign-in");
    const sent = init.headers as Headers;
    expect(sent.get("x-gozleme-client")).toBe("web");
    expect(sent.get("cookie")).toBe("gozleme_session=old; other=1");
    expect(sent.get("x-forwarded-for")).toBe("203.0.113.5");
    expect(sent.get("authorization")).toBeNull(); // only the allow-listed headers travel
    expect(init.body).toBe('{"email":"a@b.test"}');
    expect(init.redirect).toBe("manual");

    expect(res.status).toBe(200);
    expect(res.headers.getSetCookie()).toEqual(["gozleme_session=abc; path=/; httponly; samesite=lax"]);
    expect(res.headers.get("cache-control")).toBe("no-store");
    expect(await res.text()).toBe('{"userId":"x"}');
  });

  it("maps network failures and server errors to a generic 503 without internals", async () => {
    const failing = vi.fn(async () => {
      throw new TypeError("connect ECONNREFUSED 10.0.0.7:8080");
    });
    const down = await forwardToApi(incoming(), "/api/v1/auth/sign-in", { fetchImpl: failing as unknown as typeof fetch });
    expect(down.status).toBe(503);
    expect(await down.text()).toBe('{"error":"unavailable"}');

    const crashing = vi.fn(async () => new Response("", { status: 500 }));
    expect((await forwardToApi(incoming(), "/x", { fetchImpl: crashing as unknown as typeof fetch })).status).toBe(503);
  });

  it("passes 204 through without a body", async () => {
    const fetchImpl = vi.fn(async () => new Response(null, { status: 204 }));
    const res = await forwardToApi(incoming({ body: "" }), "/api/v1/auth/sign-out", { fetchImpl: fetchImpl as unknown as typeof fetch });
    expect(res.status).toBe(204);
    expect((fetchImpl.mock.calls[0]! as unknown as [URL, RequestInit])[1].body).toBeUndefined();
  });
});
