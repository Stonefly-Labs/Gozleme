import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { BADGE_WORDS, childDetail } from "@/lib/children.fixtures";
import { DECLARED_NOT_VERIFIED, DUPLICATE_ENTRY } from "@/lib/children";
import { CANONICAL_CAP, CAP, ORG, PUB } from "@/lib/revisions.fixtures";
import { ChildDetail } from "./ChildDetail";

const renderDetail = (detail = childDetail()) => render(<ChildDetail organizationId={ORG} publisherId={PUB} capabilityId={CAP} detail={detail} />);

describe("ChildDetail (GOZ-0008 US1, US2, US5)", () => {
  it("shows kind, child ID and parent capability", () => {
    renderDetail();
    expect(screen.getByTestId("child-kind")).toHaveTextContent("Tool");
    expect(screen.getByTestId("child-canonical-id").textContent).toMatch(new RegExp(`^${CANONICAL_CAP}/tool/[0-9a-f-]{36}$`));
    expect(screen.getByTestId("child-capability-id")).toHaveTextContent(CANONICAL_CAP);
    expect(screen.getByTestId("child-capability-name")).toHaveTextContent("Weather");
  });

  it("shows Present with the name and title there, and Absent, in publication order", () => {
    renderDetail();
    const rows = screen.getAllByTestId("history-row");
    expect(rows.map((r) => within(r).getByTestId("history-sequence").textContent)).toEqual(["1", "2"]);
    expect(rows.map((r) => within(r).getByTestId("history-presence").textContent)).toEqual(["Present", "Absent"]);
    expect(within(rows[0]!).getByTestId("history-name")).toHaveTextContent("search");
    expect(within(rows[0]!).getByTestId("history-title")).toHaveTextContent("Search");
    expect(within(rows[1]!).queryByTestId("history-name")).not.toBeInTheDocument();
    expect(within(rows[1]!).getByRole("link", { name: "1.1.0" })).toHaveAttribute("href", `/organizations/${ORG}/publishers/${PUB}/capabilities/${CAP}/revisions/0193a000-0000-7000-8000-000000000022`);
  });

  it("shows the fixed notice and no badge or status beyond present and absent", () => {
    const { container } = renderDetail();
    expect(screen.getByTestId("declared-notice")).toHaveTextContent(DECLARED_NOT_VERIFIED);
    expect(container.querySelector(".badge")).toBeNull();
    const text = container.textContent!.replace(DECLARED_NOT_VERIFIED, "").toLowerCase();
    for (const word of BADGE_WORDS) expect(text).not.toContain(word);
    expect(screen.queryByTestId("duplicate-note")).not.toBeInTheDocument();
  });

  it("notes a duplicate entry child (FR-016a)", () => {
    renderDetail(childDetail({ duplicate: true }));
    expect(screen.getByTestId("duplicate-note")).toHaveTextContent(DUPLICATE_ENTRY);
  });

  it("pages the history with the API's next cursor", () => {
    renderDetail({ ...childDetail(), next: 2 });
    expect(screen.getByTestId("history-next").getAttribute("href")).toMatch(/\/children\/[0-9a-f-]{36}\?after=2$/);
  });
});
