import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MANIFEST_MESSAGES } from "@/lib/manifests";
import { ManifestValidator } from "./ManifestValidator";

const ORG = "0193a000-0000-7000-8000-000000000002";
const PUB = "0193a000-0000-7000-8000-000000000010";
const CAP = "0193a000-0000-7000-8000-000000000011";
const CANONICAL = `gz://registry.demo.gozleme.test/cap/${CAP}`;

const accepted = {
  outcome: "accepted",
  envelope: { envelopeVersion: 1, family: "model", capability: CANONICAL, version: "1.4.0", native: { format: "gozleme-model-card", formatVersion: "1", encoding: "json" } },
  native: { format: "gozleme-model-card", formatVersion: "1", encoding: "json", document: '{"name":"forecast-small"}\n', digest: `sha256:${"a".repeat(64)}`, byteLength: 26 },
  projection: {
    projectionVersion: "1",
    adapter: "model/1",
    family: "model",
    kind: "model",
    nativeName: "forecast-small",
    title: "forecast-small",
    description: "d",
    declaredVersion: "1.4.0",
    attributes: { mode: "stateless" },
    entries: [],
    derived: ["/title", "/attributes/mode"],
  },
};

const respond = (status: number, body: unknown) =>
  vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(body === null ? null : JSON.stringify(body), { status }));

const renderValidator = () => render(<ManifestValidator organizationId={ORG} publisherId={PUB} capabilityId={CAP} canonicalId={CANONICAL} />);

describe("ManifestValidator (GOZ-0006 US1)", () => {
  afterEach(() => vi.restoreAllMocks());

  it("lists the six families", () => {
    renderValidator();
    const picker = screen.getByTestId("family-picker");
    expect(within(picker).getAllByRole("radio").map((r) => (r as HTMLInputElement).value)).toEqual(["mcp", "a2a", "api", "event", "skill", "model"]);
  });

  it("loads a sample with the canonical ID substituted before display", () => {
    renderValidator();
    fireEvent.click(screen.getByTestId("family-model"));
    fireEvent.click(screen.getByTestId("load-sample"));
    const text = (screen.getByTestId("manifest-text") as HTMLTextAreaElement).value;
    expect(text).toContain(`"capability": "${CANONICAL}"`);
    expect(text).not.toContain("{{capability}}");
    expect(JSON.parse(text).family).toBe("model");
  });

  it("posts the text unchanged and renders three labelled panels", async () => {
    const spy = respond(200, accepted);
    renderValidator();
    fireEvent.change(screen.getByTestId("manifest-text"), { target: { value: "  {\"exact\" : true}\n" } });
    fireEvent.click(screen.getByTestId("validate"));
    expect(await screen.findByTestId("manifest-accepted")).toBeInTheDocument();
    const [url, init] = spy.mock.calls[0]! as [string, RequestInit];
    expect(url).toBe(`/api/organizations/${ORG}/publishers/${PUB}/capabilities/${CAP}/manifests/validate`);
    expect(init.body).toBe("  {\"exact\" : true}\n");
    expect(within(screen.getByTestId("panel-envelope")).getByRole("heading")).toHaveTextContent("Envelope");
    expect(within(screen.getByTestId("panel-native")).getByRole("heading")).toHaveTextContent("Native document");
    expect(screen.getByTestId("native-digest")).toHaveTextContent(/^sha256:/);
    expect(screen.getByTestId("native-document").textContent).toBe('{"name":"forecast-small"}\n');
    expect(within(screen.getByTestId("panel-projection")).getByRole("heading", { level: 2 })).toHaveTextContent("Projection");
    expect(screen.getByTestId("projection-version")).toHaveTextContent("projection v1");
    expect(screen.getByTestId("attribute-mode")).toHaveTextContent(/^modestateless/);
    expect(within(screen.getByTestId("attribute-mode")).getByTestId("derived-marker")).toBeInTheDocument();
  });

  // A rejection is 400 (malformed), 413 (too large) or 422 (anything else), always with the result body (T098).
  it.each([
    [422, "identity_mismatch", "identity.capability"],
    [400, "malformed", "parse.syntax"],
    [413, "too_large", "bound.native-size"],
  ])("renders the problem list for a rejection answered %i", async (status, category, rule) => {
    respond(status, { outcome: "rejected", problems: [{ category, rule, location: { part: "envelope", pointer: "/capability" }, message: "M." }], truncated: false });
    renderValidator();
    fireEvent.change(screen.getByTestId("manifest-text"), { target: { value: "{}" } });
    fireEvent.click(screen.getByTestId("validate"));
    expect(await screen.findByTestId("problem-list")).toHaveTextContent(category);
  });

  it("does not show a rejection body answered 200 as a result", async () => {
    respond(200, { outcome: "rejected", problems: [], truncated: false });
    renderValidator();
    fireEvent.change(screen.getByTestId("manifest-text"), { target: { value: "{}" } });
    fireEvent.click(screen.getByTestId("validate"));
    expect(await screen.findByText(MANIFEST_MESSAGES.unavailable)).toBeInTheDocument();
  });

  it.each([
    [403, { error: "forbidden" }, MANIFEST_MESSAGES.forbidden],
    [409, { error: "suspended" }, MANIFEST_MESSAGES.suspended],
    [429, { error: "rate_limited" }, MANIFEST_MESSAGES.rate_limited],
    [503, { error: "unavailable" }, MANIFEST_MESSAGES.unavailable],
    [404, null, MANIFEST_MESSAGES.not_found],
    [413, { error: "too_large" }, MANIFEST_MESSAGES.too_large],
  ])("shows the message for %i", async (status, body, message) => {
    respond(status, body);
    renderValidator();
    fireEvent.change(screen.getByTestId("manifest-text"), { target: { value: "{}" } });
    fireEvent.click(screen.getByTestId("validate"));
    await waitFor(() => expect(screen.getByTestId("manifest-error")).toHaveTextContent(message));
    expect(screen.queryByTestId("manifest-accepted")).not.toBeInTheDocument();
  });

  it("reads a network failure as unavailable", async () => {
    vi.spyOn(globalThis, "fetch").mockRejectedValue(new TypeError("offline"));
    renderValidator();
    fireEvent.change(screen.getByTestId("manifest-text"), { target: { value: "{}" } });
    fireEvent.click(screen.getByTestId("validate"));
    await waitFor(() => expect(screen.getByTestId("manifest-error")).toHaveTextContent(MANIFEST_MESSAGES.unavailable));
  });
});
