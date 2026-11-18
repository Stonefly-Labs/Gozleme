import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { ACCOUNT, CAP, ORG, PUB, detail } from "@/lib/revisions.fixtures";
import { UNATTESTED } from "@/lib/revisions";
import { RevisionDetail } from "./RevisionDetail";

describe("RevisionDetail (GOZ-0007 SEC-1)", () => {
  it("shows the account and validation versions apart from the attested record, labelled as not attested", () => {
    render(<RevisionDetail organizationId={ORG} publisherId={PUB} capabilityId={CAP} revision={detail()} />);
    const unattested = screen.getByTestId("unattested-fields");
    expect(within(unattested).getByRole("heading", { name: "Recorded, not attested" })).toBeInTheDocument();
    expect(unattested).toHaveTextContent(UNATTESTED);
    expect(within(unattested).getByTestId("revision-published-by")).toHaveTextContent(ACCOUNT);
    expect(unattested).toHaveTextContent(/rules v\d+ · schemas v\d+/);
    const attested = screen.getByRole("region", { name: /^Revision 1/ });
    expect(attested).not.toHaveTextContent(/rules v|By account/);
  });
});
