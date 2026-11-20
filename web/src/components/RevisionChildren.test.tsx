import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { BADGE_WORDS, childId, revisionChild } from "@/lib/children.fixtures";
import { DECLARED_NOT_VERIFIED, NO_REVISION_CHILDREN } from "@/lib/children";
import { CAP, ORG, PUB, REV } from "@/lib/revisions.fixtures";
import { RevisionChildren } from "./RevisionChildren";

const renderSection = (
  items = [revisionChild(0, "tool", "search", "Search"), revisionChild(1, "tool", "fetch"), revisionChild(2, "resource", "docs"), revisionChild(3, "prompt", "summarize")],
  next: number | null = null,
) => render(<RevisionChildren organizationId={ORG} publisherId={PUB} capabilityId={CAP} revisionId={REV} list={{ items, next, declaredNotVerified: true }} />);

describe("RevisionChildren (GOZ-0008 US1)", () => {
  it("lists the revision's entries in projection order with kind, names, titles and linked child IDs", () => {
    renderSection();
    const rows = screen.getAllByTestId("revision-child-row");
    expect(rows.map((r) => within(r).getByTestId("revision-child-kind").textContent)).toEqual(["Tool", "Tool", "Resource", "Prompt"]);
    expect(rows.map((r) => within(r).getByTestId("revision-child-name").textContent)).toEqual(["search", "fetch", "docs", "summarize"]);
    expect(within(rows[0]!).getByTestId("revision-child-title")).toHaveTextContent("Search");
    expect(within(rows[1]!).getByTestId("revision-child-title")).toHaveTextContent("—");
    expect(new Set(rows.map((r) => within(r).getByTestId("revision-child-canonical-id").textContent)).size).toBe(4);
    expect(within(rows[3]!).getByTestId("revision-child-link")).toHaveAttribute("href", `/organizations/${ORG}/publishers/${PUB}/capabilities/${CAP}/children/${childId(4)}`);
  });

  it("shows the fixed notice and no badge", () => {
    const { container } = renderSection();
    expect(screen.getByTestId("declared-notice")).toHaveTextContent(DECLARED_NOT_VERIFIED);
    expect(container.querySelector(".badge")).toBeNull();
    const text = container.textContent!.replace(DECLARED_NOT_VERIFIED, "").toLowerCase();
    for (const word of BADGE_WORDS) expect(text).not.toContain(word);
  });

  it("states that a revision without entries declares no children", () => {
    renderSection([]);
    expect(screen.getByTestId("no-revision-children")).toHaveTextContent(NO_REVISION_CHILDREN);
  });

  it("marks a duplicate entry", () => {
    renderSection([revisionChild(0, "skill", "s"), revisionChild(1, "skill", "s", null, true)]);
    expect(screen.getAllByTestId("revision-child-duplicate")).toHaveLength(1);
  });

  it("links the next page from the cursor and omits it on the last page (FR-013)", () => {
    const { unmount } = renderSection(undefined, 3);
    expect(screen.getByTestId("revision-children-next")).toHaveAttribute("href", `/organizations/${ORG}/publishers/${PUB}/capabilities/${CAP}/revisions/${REV}?after=3`);
    unmount();
    renderSection();
    expect(screen.queryByTestId("revision-children-next")).not.toBeInTheDocument();
  });
});
