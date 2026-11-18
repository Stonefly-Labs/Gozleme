import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { BANNED, key, ORG, PUB } from "@/lib/revisions.fixtures";
import { COMPROMISED_KEYS, NO_KEY_YET, SUCCESSION_NOTE } from "@/lib/revisions";
import { SigningKeyList } from "./SigningKeyList";

const renderKeys = (keys = [key(1)]) => render(<SigningKeyList organizationId={ORG} publisherId={PUB} keys={keys} />);

const succession = () => {
  const second = key(2);
  const third = key(3);
  return [
    key(1, { status: "retired", retiredAt: "2026-11-18T11:00:00Z", retiredReason: "lost", successorId: second.id }),
    { ...second, status: "retired" as const, retiredAt: "2026-11-18T12:00:00Z", retiredReason: "compromised" as const, successorId: third.id },
    third,
  ];
};

describe("SigningKeyList (GOZ-0007 US3 T058)", () => {
  it("says no key yet before the first publication", () => {
    renderKeys([]);
    expect(screen.getByTestId("no-key-yet")).toHaveTextContent(NO_KEY_YET);
    expect(screen.queryAllByTestId("signing-key-row")).toHaveLength(0);
  });

  it("lists generations with key ID, algorithm, status, reason and successor", () => {
    renderKeys(succession());
    const rows = screen.getAllByTestId("signing-key-row");
    expect(rows.map((r) => r.dataset.keyStatus)).toEqual(["retired", "retired", "active"]);
    expect(within(rows[0]!).getByTestId("signing-key-id")).toHaveTextContent(`sha256:${"1".repeat(64)}`);
    expect(within(rows[0]!).getByTestId("signing-key-status")).toHaveTextContent("Retired: lost");
    expect(within(rows[0]!).getByTestId("signing-key-successor")).toHaveTextContent("successor: generation 2");
    expect(within(rows[1]!).getByTestId("signing-key-status")).toHaveTextContent("Retired: compromised");
    expect(within(rows[2]!).getByTestId("signing-key-status")).toHaveTextContent("Active");
    expect(within(rows[2]!).getByRole("link", { name: "PEM" })).toHaveAttribute("href", `/api/organizations/${ORG}/publishers/${PUB}/signing-keys/${key(3).id}/public-key`);
    expect(screen.getByTestId("compromised-banner")).toHaveTextContent(COMPROMISED_KEYS);
  });

  it("describes succession as a local operator action only, with no control", () => {
    const { container } = renderKeys(succession());
    expect(screen.getByTestId("succession-note")).toHaveTextContent(SUCCESSION_NOTE);
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
    const text = container.textContent!.toLowerCase();
    for (const banned of [...BANNED, "rotat", "replac"]) expect(text).not.toContain(banned);
  });

  it("shows no compromised banner without a compromised key", () => {
    renderKeys([key(1)]);
    expect(screen.queryByTestId("compromised-banner")).not.toBeInTheDocument();
  });
});
