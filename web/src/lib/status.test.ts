import { describe, expect, it, vi } from "vitest";
import { healthy } from "@/test/fixtures";
import { fetchBackendStatus, parseServiceStatus } from "./status";

const now = () => new Date("2026-11-14T12:00:01Z");
const json = (body: unknown, status = 200) =>
  Promise.resolve(new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } }));

describe("parseServiceStatus", () => {
  it("accepts a valid v1 document", () => {
    expect(parseServiceStatus(healthy)).toEqual(healthy);
  });

  it("accepts capabilities without the optional vector entry", () => {
    const doc = { ...healthy, dependencies: [{ ...healthy.dependencies[0]!, capabilities: {} }] };
    expect(parseServiceStatus(doc)).toEqual(doc);
  });

  it.each([
    ["malformed vector capability", { ...healthy, dependencies: [{ ...healthy.dependencies[0]!, capabilities: { vector: { available: "yes" } } }] }],
    ["wrong contract version", { ...healthy, contractVersion: "2" }],
    ["unknown status", { ...healthy, status: "fine" }],
    ["missing dependencies", { ...healthy, dependencies: undefined }],
    ["malformed dependency", { ...healthy, dependencies: [{ name: "database" }] }],
    ["not an object", "healthy"],
  ])("rejects %s", (_name, doc) => {
    expect(parseServiceStatus(doc)).toBeNull();
  });
});

describe("fetchBackendStatus", () => {
  it("forwards the given headers (session cookie)", async () => {
    const fetchImpl = vi.fn(() => json(healthy)) as unknown as typeof fetch;
    await fetchBackendStatus("http://api:8080", fetchImpl, 1000, now, { cookie: "gozleme_session=k" });
    const init = (fetchImpl as unknown as ReturnType<typeof vi.fn>).mock.calls[0]![1] as RequestInit;
    expect(init.headers).toEqual({ cookie: "gozleme_session=k" });
  });

  it("reports an expired session for 401 and an unverifiable one for 503 unavailable", async () => {
    const r401 = vi.fn(() => Promise.resolve(new Response(null, { status: 401 }))) as unknown as typeof fetch;
    expect(await fetchBackendStatus("http://api:8080", r401, 1000, now)).toEqual({ reachable: true, checkedAt: "2026-11-14T12:00:01.000Z", session: "expired" });
    const r503 = vi.fn(() => json({ error: "unavailable" }, 503)) as unknown as typeof fetch;
    expect(await fetchBackendStatus("http://api:8080", r503, 1000, now)).toEqual({ reachable: true, checkedAt: "2026-11-14T12:00:01.000Z", session: "unverifiable" });
    const other503 = vi.fn(() => Promise.resolve(new Response("oops", { status: 503 }))) as unknown as typeof fetch;
    expect(await fetchBackendStatus("http://api:8080", other503, 1000, now)).toMatchObject({ reachable: false, reason: "invalid-response" });
  });

  it("returns reachable status for a valid response", async () => {
    const fetchImpl = vi.fn(() => json(healthy)) as unknown as typeof fetch;
    const result = await fetchBackendStatus("http://api:8080", fetchImpl, 1000, now);
    expect(result).toEqual({ reachable: true, checkedAt: "2026-11-14T12:00:01.000Z", status: healthy });
    expect(String((fetchImpl as unknown as ReturnType<typeof vi.fn>).mock.calls[0]![0])).toBe("http://api:8080/api/v1/status");
  });

  it("reports unreachable on network error", async () => {
    const fetchImpl = vi.fn(() => Promise.reject(new TypeError("fetch failed"))) as unknown as typeof fetch;
    expect(await fetchBackendStatus("http://api:8080", fetchImpl, 1000, now)).toMatchObject({ reachable: false, reason: "unreachable" });
  });

  it("reports timeout when the API hangs", async () => {
    const hang: typeof fetch = (_input, init) =>
      new Promise((_resolve, reject) => {
        init?.signal?.addEventListener("abort", () => reject(init.signal!.reason));
      });
    expect(await fetchBackendStatus("http://api:8080", hang, 20, now)).toMatchObject({ reachable: false, reason: "timeout" });
  });

  it.each([
    ["non-2xx", () => json(healthy, 500)],
    ["malformed body", () => json({ hello: "world" })],
    ["non-JSON body", () => Promise.resolve(new Response("<html>", { status: 200 }))],
  ])("reports invalid-response for %s", async (_name, impl) => {
    const result = await fetchBackendStatus("http://api:8080", vi.fn(impl) as unknown as typeof fetch, 1000, now);
    expect(result).toMatchObject({ reachable: false, reason: "invalid-response" });
  });
});
