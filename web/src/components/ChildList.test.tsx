import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { BADGE_WORDS, childId, childSummary } from "@/lib/children.fixtures";
import { DECLARED_NOT_VERIFIED, NO_CHILDREN } from "@/lib/children";
import { CAP, ORG, PUB } from "@/lib/revisions.fixtures";
import { ChildList } from "./ChildList";

const renderList = (items = [childSummary(1, "tool", "search", "Search"), childSummary(2, "resource-template", "files"), childSummary(3, "prompt", "summarize")], next: number | null = null) =>
  render(<ChildList organizationId={ORG} publisherId={PUB} capabilityId={CAP} list={{ items, next, declaredNotVerified: true }} />);

describe("ChildList (GOZ-0008 US1)", () => {
  it("lists kind, native name, title and child ID, each linked to its history", () => {
    renderList();
    const rows = screen.getAllByTestId("child-row");
    expect(rows.map((r) => within(r).getByTestId("child-kind").textContent)).toEqual(["Tool", "Resource template", "Prompt"]);
    expect(rows.map((r) => within(r).getByTestId("child-name").textContent)).toEqual(["search", "files", "summarize"]);
    expect(within(rows[0]!).getByTestId("child-title")).toHaveTextContent("Search");
    expect(within(rows[1]!).getByTestId("child-title")).toHaveTextContent("—");
    expect(within(rows[0]!).getByTestId("child-canonical-id")).toHaveTextContent(/^gz:\/\/registry\.demo\.gozleme\.test\/cap\/[0-9a-f-]{36}\/tool\/[0-9a-f-]{36}$/);
    expect(within(rows[0]!).getByTestId("child-link")).toHaveAttribute("href", `/organizations/${ORG}/publishers/${PUB}/capabilities/${CAP}/children/${childId(1)}`);
  });

  it("shows the fixed notice and no badge", () => {
    const { container } = renderList();
    expect(screen.getByTestId("declared-notice")).toHaveTextContent(DECLARED_NOT_VERIFIED);
    expect(container.querySelector(".badge")).toBeNull();
    const text = container.textContent!.replace(DECLARED_NOT_VERIFIED, "").toLowerCase();
    for (const word of BADGE_WORDS) expect(text).not.toContain(word);
  });

  it("states that a capability without entries declares no children, with the notice", () => {
    renderList([]);
    expect(screen.getByTestId("no-children")).toHaveTextContent(NO_CHILDREN);
    expect(screen.getByTestId("declared-notice")).toBeInTheDocument();
    expect(screen.queryByTestId("child-row")).not.toBeInTheDocument();
  });

  it("follows the API's next cursor", () => {
    renderList(undefined, 3);
    expect(screen.getByTestId("children-next")).toHaveAttribute("href", `/organizations/${ORG}/publishers/${PUB}/capabilities/${CAP}/children?after=3`);
  });
});
