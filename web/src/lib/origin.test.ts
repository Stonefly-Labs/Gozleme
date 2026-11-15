import { afterEach, describe, expect, it, vi } from "vitest";
import { isSameOrigin } from "./origin";

const req = (headers: Record<string, string>) => new Request("http://web:3000/api/auth/sign-in", { method: "POST", headers });

describe("isSameOrigin (FR-014, CSRF layer 1)", () => {
  afterEach(() => vi.unstubAllEnvs());

  it("trusts Fetch Metadata when present", () => {
    expect(isSameOrigin(req({ "sec-fetch-site": "same-origin" }))).toBe(true);
    expect(isSameOrigin(req({ "sec-fetch-site": "same-site" }))).toBe(false);
    expect(isSameOrigin(req({ "sec-fetch-site": "cross-site", origin: "http://localhost:3000", host: "localhost:3000" }))).toBe(false);
    expect(isSameOrigin(req({ "sec-fetch-site": "none" }))).toBe(false);
  });

  it("falls back to Origin equal to the request's own origin", () => {
    expect(isSameOrigin(req({ origin: "http://localhost:3000", host: "localhost:3000" }))).toBe(true);
    expect(isSameOrigin(req({ origin: "https://gozleme.example", host: "web:3000", "x-forwarded-host": "gozleme.example", "x-forwarded-proto": "https" }))).toBe(true);
    expect(isSameOrigin(req({ origin: "http://evil.example", host: "localhost:3000" }))).toBe(false);
    expect(isSameOrigin(req({ origin: "null", host: "localhost:3000" }))).toBe(false);
  });

  it("prefers the configured public origin", () => {
    vi.stubEnv("GOZLEME_PUBLIC_ORIGIN", "https://registry.example");
    expect(isSameOrigin(req({ origin: "https://registry.example", host: "anything" }))).toBe(true);
    expect(isSameOrigin(req({ origin: "http://anything", host: "anything" }))).toBe(false);
  });

  it("refuses requests with neither Fetch Metadata nor Origin", () => {
    expect(isSameOrigin(req({ host: "localhost:3000" }))).toBe(false);
  });
});
