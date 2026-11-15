import { describe, expect, it } from "vitest";
import { safeReturnTo } from "./returnTo";

describe("safeReturnTo (FR-015)", () => {
  it.each([
    ["/", "/"],
    ["/dashboard?tab=1#x", "/dashboard?tab=1#x"],
    ["/a/../b", "/b"],
  ])("keeps same-site relative path %s", (input, expected) => {
    expect(safeReturnTo(input)).toBe(expected);
  });

  it.each([
    null,
    undefined,
    "",
    "https://evil.example/",
    "//evil.example/",
    "/\\evil.example",
    "\\\\evil.example",
    "javascript:alert(1)",
    "/%2F%2Fevil.example".replace("%2F%2F", "//"),
    "/\u0000x",
    "/\tx",
    "evil",
  ])("rejects %s", (input) => {
    expect(safeReturnTo(input)).toBe("/");
  });
});
