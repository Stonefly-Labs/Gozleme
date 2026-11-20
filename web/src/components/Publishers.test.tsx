import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Capability, Publisher } from "@/lib/publishers";
import { CapabilityList } from "./CapabilityList";
import { CreateCapabilityForm } from "./CreateCapabilityForm";
import { PublisherList } from "./PublisherList";
import { RegisterPublisherForm } from "./RegisterPublisherForm";
import { RenameDisplayNameForm } from "./RenameDisplayNameForm";

const router = { push: vi.fn(), replace: vi.fn(), refresh: vi.fn() };
vi.mock("next/navigation", () => ({ useRouter: () => router }));

const ORG = "0193a000-0000-7000-8000-000000000002";
const PUB = "0193a000-0000-7000-8000-000000000005";
const CAP = "0193a000-0000-7000-8000-000000000009";
const ORIGIN = "registry.demo.gozleme.test";
const publisher: Publisher = {
  id: PUB,
  canonicalId: `gz://${ORIGIN}/pub/${PUB}`,
  origin: ORIGIN,
  organizationId: ORG,
  handle: "acme-tools",
  displayName: "Acme Tools",
  state: "active",
  createdAt: "2026-11-19T10:00:00Z",
  updatedAt: "2026-11-19T10:00:00Z",
};
const capability: Capability = {
  id: CAP,
  canonicalId: `gz://${ORIGIN}/cap/${CAP}`,
  origin: ORIGIN,
  publisherId: PUB,
  alias: "acme-tools/weather-lookup",
  name: "weather-lookup",
  displayName: "Weather Lookup",
  state: "active",
  createdAt: "2026-11-19T10:00:00Z",
  updatedAt: "2026-11-19T10:00:00Z",
};

function respond(status: number, body?: unknown) {
  return vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(body === undefined ? null : JSON.stringify(body), { status }));
}

const sent = (spy: ReturnType<typeof respond>) => {
  const [url, init] = spy.mock.calls[0]! as [string, RequestInit];
  return { url, method: init.method, body: init.body ? JSON.parse(init.body as string) : undefined };
};

beforeEach(() => vi.clearAllMocks());
afterEach(() => vi.restoreAllMocks());

describe("RegisterPublisherForm", () => {
  it("starts with an empty handle and no suggestion taken from the organization or an email (FR-010)", () => {
    render(<RegisterPublisherForm organizationId={ORG} />);
    const handle = screen.getByLabelText("Handle");
    expect(handle).toHaveValue("");
    expect(handle).not.toHaveAttribute("placeholder");
    expect(handle).toHaveAttribute("autocomplete", "off");
    expect(screen.getByText(/lowercase letters, digits and single hyphens/)).toBeInTheDocument();
  });

  it("sends the handle exactly as typed and confirms with the organization id and the publisher id", async () => {
    const spy = respond(201, publisher);
    render(<RegisterPublisherForm organizationId={ORG} />);
    fireEvent.change(screen.getByLabelText("Handle"), { target: { value: "acme-tools" } });
    fireEvent.change(screen.getByLabelText("Display name"), { target: { value: "Acme Tools" } });
    fireEvent.click(screen.getByRole("button", { name: "Register publisher" }));
    expect(await screen.findByTestId("confirmed-publisher-id")).toHaveTextContent(publisher.canonicalId);
    expect(screen.getByTestId("confirmed-organization")).toHaveTextContent(ORG);
    expect(screen.getByTestId("confirmed-handle")).toHaveTextContent("acme-tools");
    expect(sent(spy)).toEqual({ url: `/api/organizations/${ORG}/publishers`, method: "POST", body: { handle: "acme-tools", displayName: "Acme Tools" } });
    expect(router.refresh).toHaveBeenCalled();
  });

  it("never normalizes: an uppercase handle is sent unchanged and the API's rule is shown", async () => {
    const rule = "Use 2–39 characters: lowercase letters a–z, digits 0–9 and single hyphens.";
    const spy = respond(400, { error: "validation_failed", fields: { handle: [rule] } });
    render(<RegisterPublisherForm organizationId={ORG} />);
    fireEvent.change(screen.getByLabelText("Handle"), { target: { value: " Acme " } });
    fireEvent.click(screen.getByRole("button", { name: "Register publisher" }));
    expect(await screen.findByText(rule)).toBeInTheDocument();
    expect(sent(spy).body.handle).toBe(" Acme ");
    expect(screen.getByLabelText("Handle")).toHaveAttribute("aria-invalid", "true");
  });

  it.each([
    [409, { error: "handle_unavailable" }, "That handle isn't available."],
    [429, { error: "rate_limited" }, "Too many registrations. Try again later."],
    [503, { error: "unavailable" }, "The service is unavailable. Try again shortly."],
  ])("shows one neutral message for %i", async (status, body, text) => {
    respond(status, body);
    render(<RegisterPublisherForm organizationId={ORG} />);
    fireEvent.click(screen.getByRole("button", { name: "Register publisher" }));
    expect(await screen.findByTestId("register-error")).toHaveTextContent(text);
    expect(screen.queryByTestId("register-confirmation")).not.toBeInTheDocument();
  });

  it("a 403 shows the permission message and reloads the organization with a notice", async () => {
    respond(403, { error: "forbidden" });
    render(<RegisterPublisherForm organizationId={ORG} />);
    fireEvent.click(screen.getByRole("button", { name: "Register publisher" }));
    expect(await screen.findByText("You no longer have permission to do that.")).toBeInTheDocument();
    expect(router.replace).toHaveBeenCalledWith(`/organizations/${ORG}?notice=forbidden`);
  });
});

describe("PublisherList", () => {
  it("lists handles, display names, publisher ids and states with links by opaque id", () => {
    render(<PublisherList organizationId={ORG} publishers={[publisher, { ...publisher, id: "p2", canonicalId: `gz://${ORIGIN}/pub/p2`, handle: "squatter", state: "suspended" }]} />);
    expect(screen.getAllByTestId("publisher-row")).toHaveLength(2);
    expect(screen.getByRole("link", { name: "acme-tools" })).toHaveAttribute("href", `/organizations/${ORG}/publishers/${PUB}`);
    expect(screen.getByText(publisher.canonicalId)).toBeInTheDocument();
    expect(screen.getByTestId("suspended-badge")).toHaveTextContent("Suspended");
  });

  it("says when there are none", () => {
    render(<PublisherList organizationId={ORG} publishers={[]} />);
    expect(screen.getByText("No publishers yet.")).toBeInTheDocument();
  });
});

describe("CreateCapabilityForm", () => {
  it("creates under the publisher's opaque id and refreshes", async () => {
    const spy = respond(201, capability);
    render(<CreateCapabilityForm organizationId={ORG} publisherId={PUB} />);
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "weather-lookup" } });
    fireEvent.change(screen.getByLabelText("Display name"), { target: { value: "Weather Lookup" } });
    fireEvent.click(screen.getByRole("button", { name: "Create capability" }));
    await waitFor(() => expect(router.refresh).toHaveBeenCalled());
    expect(sent(spy)).toEqual({
      url: `/api/organizations/${ORG}/publishers/${PUB}/capabilities`,
      method: "POST",
      body: { name: "weather-lookup", displayName: "Weather Lookup" },
    });
  });

  it.each([
    [{ error: "conflict" }, "That name is already used under this publisher."],
    [{ error: "suspended" }, "This publisher is suspended. Its names and identifiers are frozen."],
  ])("explains a 409 %j", async (body, text) => {
    respond(409, body);
    render(<CreateCapabilityForm organizationId={ORG} publisherId={PUB} />);
    fireEvent.click(screen.getByRole("button", { name: "Create capability" }));
    expect(await screen.findByTestId("capability-error")).toHaveTextContent(text);
  });
});

describe("CapabilityList", () => {
  it("shows the canonical id, alias and name, with a copy button", async () => {
    const write = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText: write } });
    render(<CapabilityList organizationId={ORG} publisherId={PUB} capabilities={[capability]} canRename={false} />);
    const row = screen.getByTestId("capability-row");
    expect(within(row).getByTestId("capability-canonical-id")).toHaveTextContent(capability.canonicalId);
    expect(within(row).getByTestId("capability-alias")).toHaveTextContent("acme-tools/weather-lookup");
    expect(within(row).queryByRole("button", { name: "Rename" })).not.toBeInTheDocument();
    fireEvent.click(within(row).getByRole("button", { name: "Copy canonical ID of weather-lookup" }));
    await waitFor(() => expect(write).toHaveBeenCalledWith(capability.canonicalId));
  });

  it("offers rename only to permitted callers while active", () => {
    const { rerender } = render(<CapabilityList organizationId={ORG} publisherId={PUB} capabilities={[capability]} canRename />);
    expect(screen.getByRole("button", { name: "Rename" })).toBeInTheDocument();
    rerender(<CapabilityList organizationId={ORG} publisherId={PUB} capabilities={[{ ...capability, state: "suspended" }]} canRename />);
    expect(screen.queryByRole("button", { name: "Rename" })).not.toBeInTheDocument();
    expect(screen.getByTestId("suspended-badge")).toBeInTheDocument();
  });

  it("links to manifest validation only for callers holding manifest.validate while active (GOZ-0006)", () => {
    const { rerender } = render(<CapabilityList organizationId={ORG} publisherId={PUB} capabilities={[capability]} canRename={false} />);
    expect(screen.queryByTestId("validate-manifest-link")).not.toBeInTheDocument();
    rerender(<CapabilityList organizationId={ORG} publisherId={PUB} capabilities={[capability]} canRename={false} canValidate />);
    expect(screen.getByRole("link", { name: "Validate manifest" })).toHaveAttribute(
      "href",
      `/organizations/${ORG}/publishers/${PUB}/capabilities/${CAP}/manifest`,
    );
    rerender(<CapabilityList organizationId={ORG} publisherId={PUB} capabilities={[{ ...capability, state: "suspended" }]} canRename={false} canValidate />);
    expect(screen.queryByTestId("validate-manifest-link")).not.toBeInTheDocument();
  });
});

describe("CapabilityList children (GOZ-0008)", () => {
  it("links Children beside Revisions for callers holding manifest.list, also while suspended", () => {
    const { rerender } = render(<CapabilityList organizationId={ORG} publisherId={PUB} capabilities={[capability]} canRename={false} />);
    expect(screen.queryByTestId("children-link")).not.toBeInTheDocument();
    rerender(<CapabilityList organizationId={ORG} publisherId={PUB} capabilities={[{ ...capability, state: "suspended" }]} canRename={false} canListRevisions />);
    expect(screen.getByTestId("children-link")).toHaveAttribute("href", `/organizations/${ORG}/publishers/${PUB}/capabilities/${capability.id}/children`);
  });
});

describe("RenameDisplayNameForm", () => {
  it("sends only the display name", async () => {
    const path = `/api/organizations/${ORG}/publishers/${PUB}/capabilities/${CAP}`;
    const spy = respond(200, { ...capability, displayName: "Forecasts" });
    render(<RenameDisplayNameForm organizationId={ORG} path={path} flow="capability_rename" label="Display name" displayName="Weather Lookup" inputId="r" />);
    fireEvent.change(screen.getByLabelText("Display name"), { target: { value: "Forecasts" } });
    fireEvent.click(screen.getByRole("button", { name: "Rename" }));
    expect(await screen.findByText("Saved.")).toBeInTheDocument();
    expect(sent(spy)).toEqual({ url: path, method: "PATCH", body: { displayName: "Forecasts" } });
  });

  it("explains a suspended publisher", async () => {
    respond(409, { error: "suspended" });
    render(<RenameDisplayNameForm organizationId={ORG} path="/x" flow="publisher_rename" label="Display name" displayName="A" inputId="r" />);
    fireEvent.click(screen.getByRole("button", { name: "Rename" }));
    expect(await screen.findByText("This publisher is suspended. Its names and identifiers are frozen.")).toBeInTheDocument();
  });
});
