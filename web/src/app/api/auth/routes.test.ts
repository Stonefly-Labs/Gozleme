import { afterEach, describe, expect, it, vi } from "vitest";
import { healthy } from "@/test/fixtures";
import { GET as backendStatus } from "../backend-status/route";
import { GET as live } from "../live/route";
import { POST as register } from "./register/route";
import { POST as signIn } from "./sign-in/route";
import { POST as signOut } from "./sign-out/route";
import { POST as signOutAll } from "./sign-out-all/route";

const post = (headers: Record<string, string>) =>
  new Request("http://localhost:3000/api/auth/x", { method: "POST", body: "{}", headers: { "content-type": "application/json", ...headers } });

describe("auth route handlers", () => {
  afterEach(() => vi.restoreAllMocks());

  it.each([
    ["register", register],
    ["sign-in", signIn],
    ["sign-out", signOut],
    ["sign-out-all", signOutAll],
  ])("%s refuses cross-site requests without calling the API", async (_name, handler) => {
    const upstream = vi.spyOn(globalThis, "fetch");
    const res = await handler(post({ "sec-fetch-site": "cross-site" }));
    expect(res.status).toBe(403);
    expect(upstream).not.toHaveBeenCalled();
  });

  it("forwards same-origin sign-in and relays the session cookie", async () => {
    const headers = new Headers({ "content-type": "application/json" });
    headers.append("set-cookie", "gozleme_session=k; path=/; httponly; samesite=lax");
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response("{}", { status: 200, headers }));
    const res = await signIn(post({ "sec-fetch-site": "same-origin" }));
    expect(res.status).toBe(200);
    expect(res.headers.getSetCookie()[0]).toContain("gozleme_session=k");
    expect(String(upstream.mock.calls[0]![0])).toMatch(/\/api\/v1\/auth\/sign-in$/);
  });
});

describe("backend-status route", () => {
  afterEach(() => vi.restoreAllMocks());
  const get = (cookie?: string) => new Request("http://localhost:3000/api/backend-status", { headers: cookie ? { cookie } : {} });

  it("forwards the session cookie and returns status for a valid session", async () => {
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValue(Response.json(healthy));
    const res = await backendStatus(get("gozleme_session=k"));
    expect(res.status).toBe(200);
    expect((await res.json()).status.status).toBe("healthy");
    expect(((upstream.mock.calls[0]![1] as RequestInit).headers as Headers).get("cookie")).toBe("gozleme_session=k");
  });

  it("answers 401 with no status data when the API rejects the session", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 401 }));
    const res = await backendStatus(get());
    const body = await res.json();
    expect(res.status).toBe(401);
    expect(body).toEqual({ reachable: true, checkedAt: expect.any(String), session: "expired" });
  });

  it("answers 503 'unverifiable' with no status data while the database is down", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(Response.json({ error: "unavailable" }, { status: 503 }));
    const res = await backendStatus(get("gozleme_session=k"));
    expect(res.status).toBe(503);
    expect(await res.json()).toEqual({ reachable: true, checkedAt: expect.any(String), session: "unverifiable" });
  });
});

describe("web liveness", () => {
  it("is anonymous and independent of the API", async () => {
    const upstream = vi.spyOn(globalThis, "fetch");
    const res = live();
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({ status: "live" });
    expect(upstream).not.toHaveBeenCalled();
  });
});
