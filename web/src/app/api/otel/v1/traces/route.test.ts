import { afterEach, describe, expect, it, vi } from "vitest";
import { MAX_BODY_BYTES, POST } from "./route";

const req = (body: BodyInit, type = "application/json", headers: Record<string, string> = {}) =>
  new Request("http://web/api/otel/v1/traces", { method: "POST", body, headers: { "content-type": type, ...headers } });

describe("browser OTLP ingestion route", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.restoreAllMocks();
  });

  it("forwards bounded JSON trace payloads to the collector", async () => {
    vi.stubEnv("OTEL_EXPORTER_OTLP_ENDPOINT", "http://otel-collector:4318");
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 200 }));
    const res = await POST(req('{"resourceSpans":[]}'));
    expect(res.status).toBe(202);
    const [url, init] = upstream.mock.calls[0]!;
    expect(String(url)).toBe("http://otel-collector:4318/v1/traces");
    expect((init!.headers as Record<string, string>)["content-type"]).toBe("application/json");
  });

  it("preserves a path prefix on the collector endpoint", async () => {
    vi.stubEnv("OTEL_EXPORTER_OTLP_ENDPOINT", "https://telemetry.example/otlp/");
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 200 }));
    await POST(req("{}"));
    expect(String(upstream.mock.calls[0]![0])).toBe("https://telemetry.example/otlp/v1/traces");
  });

  it("rejects unsupported content types", async () => {
    expect((await POST(req("x", "text/plain"))).status).toBe(415);
  });

  it("rejects oversized payloads by header and by actual size", async () => {
    vi.stubEnv("OTEL_EXPORTER_OTLP_ENDPOINT", "http://otel-collector:4318");
    expect((await POST(req("{}", "application/json", { "content-length": String(MAX_BODY_BYTES + 1) }))).status).toBe(413);
    expect((await POST(req("x".repeat(MAX_BODY_BYTES + 1)))).status).toBe(413);
  });

  it("accepts and drops when export is not configured", async () => {
    const upstream = vi.spyOn(globalThis, "fetch");
    expect((await POST(req("{}"))).status).toBe(204);
    expect(upstream).not.toHaveBeenCalled();
  });

  it("reports 502 without leaking detail when the collector is down", async () => {
    vi.stubEnv("OTEL_EXPORTER_OTLP_ENDPOINT", "http://otel-collector:4318");
    vi.spyOn(globalThis, "fetch").mockRejectedValue(new TypeError("connect ECONNREFUSED 10.0.0.5:4318"));
    const res = await POST(req("{}"));
    expect(res.status).toBe(502);
    expect(await res.text()).toBe("");
  });
});
