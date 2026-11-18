import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { BANNED, CAP, DIGEST, KEY, KEY_ID, ORG, PUB, summary } from "@/lib/revisions.fixtures";
import { SUSPENDED_PUBLISHER, VERIFICATION_MESSAGES } from "@/lib/revisions";
import { RevisionList } from "./RevisionList";
import { VerificationBadge } from "./VerificationBadge";

const failed = { status: "failed", reason: "native_digest_mismatch", key: { id: KEY, keyId: KEY_ID, status: "active" } } as const;

const renderList = (overrides: Partial<Parameters<typeof RevisionList>[0]["list"]> = {}) =>
  render(
    <RevisionList
      organizationId={ORG}
      publisherId={PUB}
      capabilityId={CAP}
      list={{ publisherSuspended: false, revisions: [summary(1, "2.0.0"), summary(2, "1.2.5", failed)], next: null, ...overrides }}
    />,
  );

describe("RevisionList (GOZ-0007 US1, US2)", () => {
  it("lists revisions in publication order with their records and no latest label", () => {
    const { container } = renderList();
    const rows = screen.getAllByTestId("revision-row");
    expect(rows.map((r) => within(r).getByTestId("revision-version").textContent)).toEqual(["2.0.0", "1.2.5"]);
    expect(rows.map((r) => within(r).getByTestId("revision-sequence").textContent)).toEqual(["1", "2"]);
    expect(within(rows[0]!).getByTestId("revision-published-by")).toHaveTextContent(/^0193a000-/);
    const digest = within(rows[0]!).getByTestId("revision-digest");
    expect(digest.textContent).toBe("sha256:aaaaaaaa…aaaa");
    expect(digest).toHaveAttribute("data-value", DIGEST);
    expect(within(rows[0]!).getByRole("link", { name: "2.0.0" })).toHaveAttribute("href", `/organizations/${ORG}/publishers/${PUB}/capabilities/${CAP}/revisions/${summary(1, "x").id}`);
    expect(container.textContent?.toLowerCase()).not.toContain("latest");
    expect(screen.queryByTestId("suspended-publisher-banner")).not.toBeInTheDocument();
    expect(screen.queryByTestId("revisions-next")).not.toBeInTheDocument();
  });

  it("shows each badge as computed by the API, never verified for a failure", () => {
    renderList();
    const badges = screen.getAllByTestId("verification-badge");
    expect(badges.map((b) => [b.textContent, b.dataset.status])).toEqual([
      ["Verified", "verified"],
      ["Failed", "failed"],
    ]);
  });

  it("pages with next and banners a suspended publisher", () => {
    renderList({ publisherSuspended: true, next: 50 });
    expect(screen.getByTestId("revisions-next")).toHaveAttribute("href", `/organizations/${ORG}/publishers/${PUB}/capabilities/${CAP}/revisions?after=50`);
    expect(screen.getByTestId("suspended-publisher-banner")).toHaveTextContent(SUSPENDED_PUBLISHER);
  });

  it("says when there are no revisions", () => {
    renderList({ revisions: [] });
    expect(screen.getByTestId("no-revisions")).toBeInTheDocument();
  });

  it("contains none of the banned phrases", () => {
    const { container } = renderList({ publisherSuspended: true, next: 3 });
    for (const banned of BANNED) expect(container.textContent?.toLowerCase()).not.toContain(banned);
  });
});

describe("VerificationBadge (GOZ-0007 US2 T048)", () => {
  it("shows the fixed reason and that a failed revision is never shown as verified", () => {
    render(<VerificationBadge verification={failed} detailed />);
    expect(screen.getByTestId("verification-badge")).toHaveTextContent("Failed");
    expect(screen.getByTestId("verification-badge")).toHaveAttribute("data-reason", "native_digest_mismatch");
    expect(screen.getByTestId("verification-reason")).toHaveTextContent(VERIFICATION_MESSAGES.native_digest_mismatch);
    expect(screen.getByTestId("verification-reason")).toHaveTextContent(/not shown as verified/);
    expect(screen.queryByText("Verified")).not.toBeInTheDocument();
  });

  it("notes a retired key on a verified revision", () => {
    render(<VerificationBadge verification={{ status: "verified", reason: null, key: { id: KEY, keyId: KEY_ID, status: "retired", retiredAt: "2026-11-18T11:00:00Z", retiredReason: "compromised" } }} />);
    expect(screen.getByTestId("verification-badge")).toHaveTextContent("Verified");
    expect(screen.getByTestId("key-retired-badge")).toHaveTextContent("Key retired: compromised");
  });
});
