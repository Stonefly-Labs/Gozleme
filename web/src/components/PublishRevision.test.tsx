import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { CAP, detail, ORG, PUB, REV } from "@/lib/revisions.fixtures";
import { REVISION_MESSAGES } from "@/lib/revisions";
import { PublishRevision } from "./PublishRevision";

const router = { push: vi.fn(), refresh: vi.fn() };
vi.mock("next/navigation", () => ({ useRouter: () => router }));

const BODY = '  {"envelopeVersion" : 1}\n';

const respond = (status: number, body: unknown) =>
  vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(body === null ? null : JSON.stringify(body), { status }));

const renderPublish = (canPublish = true) =>
  render(<PublishRevision organizationId={ORG} publisherId={PUB} capabilityId={CAP} body={BODY} canPublish={canPublish} />);

describe("PublishRevision (GOZ-0007 US1, US2, US5)", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    router.push.mockReset();
  });

  it("renders nothing without manifest.publish", () => {
    renderPublish(false);
    expect(screen.queryByTestId("publish-revision")).not.toBeInTheDocument();
  });

  it("posts the validated body unchanged and opens the new revision on 201", async () => {
    const spy = respond(201, detail());
    renderPublish();
    fireEvent.click(screen.getByTestId("publish"));
    await waitFor(() => expect(router.push).toHaveBeenCalledWith(`/organizations/${ORG}/publishers/${PUB}/capabilities/${CAP}/revisions/${REV}`));
    const [url, init] = spy.mock.calls[0]! as [string, RequestInit];
    expect(url).toBe(`/api/organizations/${ORG}/publishers/${PUB}/capabilities/${CAP}/revisions`);
    expect(init.method).toBe("POST");
    expect(init.body).toBe(BODY);
  });

  it("points a version_exists refusal to the revisions list", async () => {
    respond(409, { error: "version_exists" });
    renderPublish();
    fireEvent.click(screen.getByTestId("publish"));
    expect(await screen.findByTestId("publish-error")).toHaveTextContent(REVISION_MESSAGES.version_exists);
    expect(screen.getByTestId("publish-revisions-link")).toHaveAttribute("href", `/organizations/${ORG}/publishers/${PUB}/capabilities/${CAP}/revisions`);
    expect(router.push).not.toHaveBeenCalled();
  });

  it.each([
    [403, { error: "forbidden" }, REVISION_MESSAGES.forbidden],
    [409, { error: "suspended" }, REVISION_MESSAGES.suspended],
    [429, { error: "rate_limited" }, REVISION_MESSAGES.rate_limited],
    [503, { error: "unavailable" }, REVISION_MESSAGES.unavailable],
    [404, null, REVISION_MESSAGES.not_found],
    [413, { error: "too_large" }, REVISION_MESSAGES.too_large],
    [422, { outcome: "rejected", problems: [], truncated: false }, REVISION_MESSAGES.rejected],
  ])("shows the message for %i", async (status, body, message) => {
    respond(status, body);
    renderPublish();
    fireEvent.click(screen.getByTestId("publish"));
    expect(await screen.findByTestId("publish-error")).toHaveTextContent(message);
    expect(screen.queryByTestId("publish-revisions-link")).not.toBeInTheDocument();
  });

  it("shows the problem list of a publish-time rejection (GOZ-0008 publish.duplicate_entry)", async () => {
    const problem = { category: "invalid_value", rule: "publish.duplicate_entry", message: "Two or more projection entries have the same kind and native name." };
    respond(422, { outcome: "rejected", problems: [0, 3].map((i) => ({ ...problem, location: { part: "projection", pointer: `/entries/${i}` } })), truncated: false });
    renderPublish();
    fireEvent.click(screen.getByTestId("publish"));
    expect(await screen.findByTestId("publish-error")).toHaveTextContent(REVISION_MESSAGES.rejected);
    expect(screen.getAllByTestId("problem-location").map((l) => l.textContent)).toEqual(["projection /entries/0", "projection /entries/3"]);
    expect(screen.getAllByTestId("problem-rule").map((l) => l.textContent)).toEqual(["publish.duplicate_entry", "publish.duplicate_entry"]);
  });

  it("gives a truncated publish-time problem list the publish note (GOZ-0008 publish.nul_character)", async () => {
    const problem = { category: "invalid_value", rule: "publish.nul_character", location: { part: "projection", pointer: "/entries/0/title" }, message: "A projection or envelope string contains the character U+0000." };
    respond(422, { outcome: "rejected", problems: [problem], truncated: true });
    renderPublish();
    fireEvent.click(screen.getByTestId("publish"));
    expect(await screen.findByTestId("problems-truncated")).toHaveTextContent("More problems exist. Fix these and publish again to see the rest.");
    expect(screen.getByTestId("problem-location")).toHaveTextContent("projection /entries/0/title");
  });

  it("reads a network failure as unavailable", async () => {
    vi.spyOn(globalThis, "fetch").mockRejectedValue(new TypeError("offline"));
    renderPublish();
    fireEvent.click(screen.getByTestId("publish"));
    expect(await screen.findByTestId("publish-error")).toHaveTextContent(REVISION_MESSAGES.unavailable);
  });
});
