import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { Problem } from "@/lib/manifests";
import { ProblemList } from "./ProblemList";

const problems: Problem[] = [
  { category: "unknown_field", rule: "native.unknown-field", location: { part: "native", pointer: "/tools/0/execution" }, message: "This field is not defined by the declared format version." },
  { category: "secret_value", rule: "secret.pattern", location: { part: "native", line: 12, column: 1 }, message: "This value looks like a credential." },
  { category: "too_large", rule: "bound.depth", location: null, message: "The document is nested too deeply." },
];

describe("ProblemList (GOZ-0006 US2)", () => {
  it("renders category, rule, location and message for each problem", () => {
    render(<ProblemList problems={problems} truncated={false} />);
    const items = screen.getAllByTestId("problem");
    expect(items).toHaveLength(3);
    expect(within(items[0]!).getByTestId("problem-category")).toHaveTextContent("unknown_field");
    expect(within(items[0]!).getByTestId("problem-rule")).toHaveTextContent("native.unknown-field");
    expect(within(items[0]!).getByTestId("problem-location")).toHaveTextContent("native /tools/0/execution");
    expect(within(items[0]!).getByTestId("problem-message")).toHaveTextContent("This field is not defined by the declared format version.");
    expect(within(items[1]!).getByTestId("problem-location")).toHaveTextContent("native line 12, column 1");
    expect(within(items[2]!).getByTestId("problem-location")).toHaveTextContent("—");
    expect(screen.queryByTestId("problems-truncated")).not.toBeInTheDocument();
  });

  it("renders a projection location as a pointer, without echoing a name (GOZ-0008)", () => {
    const duplicate: Problem = {
      category: "invalid_value",
      rule: "publish.duplicate_entry",
      location: { part: "projection", pointer: "/entries/2" },
      message: "Two or more projection entries have the same kind and native name.",
    };
    const { container } = render(<ProblemList problems={[duplicate]} truncated={false} />);
    expect(screen.getByTestId("problem-location")).toHaveTextContent("projection /entries/2");
    expect(container.textContent).toBe(`Rejected: 1 probleminvalid_valuepublish.duplicate_entryprojection /entries/2${duplicate.message}`);
  });

  it("notes that more problems exist when truncated", () => {
    render(<ProblemList problems={problems.slice(0, 1)} truncated />);
    expect(screen.getByTestId("problems-truncated")).toHaveTextContent("More problems exist");
  });

  it("names the root pointer and never renders fields outside the problem shape", () => {
    const extra = { category: "invalid_value", rule: "native.schema", location: { part: "native", pointer: "" }, message: "M.", value: "LEAKED-VALUE" };
    render(<ProblemList problems={[extra as Problem]} truncated={false} />);
    expect(screen.getByTestId("problem-location")).toHaveTextContent("native (root)");
    expect(document.body.textContent).not.toContain("LEAKED-VALUE");
  });
});
