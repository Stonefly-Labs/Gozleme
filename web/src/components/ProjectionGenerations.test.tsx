import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { GENERATION, generation } from "@/lib/children.fixtures";
import { ATTESTED, COMPARISON_LABELS, NOT_ATTESTED } from "@/lib/children";
import { CAP, DIGEST, ORG, PUB, REV } from "@/lib/revisions.fixtures";
import { ProjectionGenerations } from "./ProjectionGenerations";

const renderSection = (items = [generation(1), generation(2)]) =>
  render(<ProjectionGenerations organizationId={ORG} publisherId={PUB} capabilityId={CAP} revisionId={REV} list={{ items }} />);

describe("ProjectionGenerations (GOZ-0008 US3)", () => {
  it("shows generation, generation ID, adapter and digest for each generation", () => {
    renderSection();
    const rows = screen.getAllByTestId("generation-row");
    expect(rows.map((r) => within(r).getByTestId("generation-number").textContent)).toEqual(["1", "2"]);
    expect(within(rows[0]!).getByTestId("generation-id")).toHaveTextContent(GENERATION);
    expect(within(rows[0]!).getByTestId("generation-adapter")).toHaveTextContent("mcp/1");
    expect(within(rows[0]!).getByTestId("generation-digest")).toHaveAttribute("data-value", DIGEST);
  });

  it("labels generation 1 attested at publication and later ones not attested", () => {
    renderSection();
    const rows = screen.getAllByTestId("generation-row");
    expect(rows.map((r) => within(r).getByTestId("generation-attested").textContent)).toEqual([ATTESTED, NOT_ATTESTED]);
    expect(within(rows[0]!).queryByTestId("generation-comparison")).not.toBeInTheDocument();
    expect(within(rows[1]!).getByTestId("generation-comparison")).toHaveTextContent(COMPARISON_LABELS.identical);
  });

  it("links each generation's download", () => {
    renderSection();
    const link = within(screen.getAllByTestId("generation-row")[0]!).getByTestId("generation-download");
    expect(link).toHaveAttribute("href", `/api/organizations/${ORG}/publishers/${PUB}/capabilities/${CAP}/revisions/${REV}/projections/${GENERATION}`);
    expect(link).toHaveAttribute("download");
  });
});
