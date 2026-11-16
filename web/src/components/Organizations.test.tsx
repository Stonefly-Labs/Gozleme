import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Invitation, Member, Organization } from "@/lib/organizations";
import { CreateOrganizationForm } from "./CreateOrganizationForm";
import { InvitationPanel } from "./InvitationPanel";
import { LAST_MEMBER, MemberList } from "./MemberList";
import { OrganizationSwitcher } from "./OrganizationSwitcher";
import { OrganizationsUnavailable } from "./OrganizationsUnavailable";
import { INVALID_INVITATION, RedeemInvitationForm } from "./RedeemInvitationForm";
import { RenameOrganizationForm } from "./RenameOrganizationForm";

const router = { push: vi.fn(), replace: vi.fn(), refresh: vi.fn() };
vi.mock("next/navigation", () => ({ useRouter: () => router }));

const ORG = "0193a000-0000-7000-8000-000000000002";
const orgs: Organization[] = [
  { id: ORG, displayName: "Demo Org Alpha", createdAt: "2026-11-15T10:00:00Z" },
  { id: "0193a000-0000-7000-8000-000000000003", displayName: "Demo Org Beta", createdAt: "2026-11-15T10:01:00Z" },
];
const alice: Member = { userId: "u-alice", email: "alice@demo.gozleme.test", joinedAt: "2026-11-15T10:00:00Z", joinedVia: "creator", isSelf: true };
const bob: Member = { userId: "u-bob", email: "bob@demo.gozleme.test", joinedAt: "2026-11-15T10:05:00Z", joinedVia: "invitation", isSelf: false };

function respond(status: number, body?: unknown) {
  return vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(body === undefined ? null : JSON.stringify(body), { status }));
}

const sent = (spy: ReturnType<typeof respond>, call = 0) => {
  const [url, init] = spy.mock.calls[call]! as [string, RequestInit];
  return { url, method: init.method, body: init.body ? JSON.parse(init.body as string) : undefined };
};

beforeEach(() => vi.clearAllMocks());
afterEach(() => vi.restoreAllMocks());

describe("CreateOrganizationForm", () => {
  it("creates and opens the new organization", async () => {
    const spy = respond(201, { id: ORG, displayName: "Demo Org Alpha", createdAt: "2026-11-15T10:00:00Z" });
    render(<CreateOrganizationForm />);
    fireEvent.change(screen.getByLabelText("Organization name"), { target: { value: "Demo Org Alpha" } });
    fireEvent.click(screen.getByRole("button", { name: "Create organization" }));
    await waitFor(() => expect(router.push).toHaveBeenCalledWith(`/organizations/${ORG}`));
    expect(sent(spy)).toEqual({ url: "/api/organizations", method: "POST", body: { displayName: "Demo Org Alpha" } });
  });

  it("shows the API's field error accessibly", async () => {
    respond(400, { error: "validation_failed", fields: { displayName: ["Enter a name of 1–100 characters."] } });
    render(<CreateOrganizationForm />);
    fireEvent.click(screen.getByRole("button", { name: "Create organization" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Enter a name of 1–100 characters.");
    expect(screen.getByLabelText("Organization name")).toHaveAttribute("aria-invalid", "true");
    expect(screen.getByLabelText("Organization name")).toHaveAttribute("maxlength", "100");
  });
});

describe("RenameOrganizationForm", () => {
  it("renames by id and refreshes", async () => {
    const spy = respond(200, { id: ORG, displayName: "Renamed", createdAt: "2026-11-15T10:00:00Z" });
    render(<RenameOrganizationForm organizationId={ORG} displayName="Demo Org Beta" />);
    fireEvent.change(screen.getByLabelText("Organization name"), { target: { value: "Renamed" } });
    fireEvent.click(screen.getByRole("button", { name: "Rename" }));
    expect(await screen.findByText("Saved.")).toBeInTheDocument();
    expect(sent(spy)).toEqual({ url: `/api/organizations/${ORG}`, method: "PATCH", body: { displayName: "Renamed" } });
  });
});

describe("OrganizationSwitcher", () => {
  it("lists only the given organizations, marks the active one and records a selection", async () => {
    const spy = respond(204);
    render(<OrganizationSwitcher organizations={orgs} activeOrganizationId={ORG} />);
    const select = screen.getByLabelText("Active organization");
    expect(select).toHaveValue(ORG);
    expect(within(select).getAllByRole("option").map((o) => o.textContent)).toEqual(["Demo Org Alpha", "Demo Org Beta"]);
    fireEvent.change(select, { target: { value: orgs[1]!.id } });
    await waitFor(() => expect(router.push).toHaveBeenCalledWith(`/organizations/${orgs[1]!.id}`));
    expect(sent(spy)).toMatchObject({ url: `/api/organizations/${orgs[1]!.id}/select`, method: "POST" });
  });

  it("renders nothing without organizations", () => {
    const { container } = render(<OrganizationSwitcher organizations={[]} activeOrganizationId={null} />);
    expect(container).toBeEmptyDOMElement();
  });
});

describe("MemberList", () => {
  it("shows email and join date, confirms before removing, and has no role controls", async () => {
    const spy = respond(204);
    render(<MemberList organizationId={ORG} members={[alice, bob]} />);
    expect(screen.getAllByTestId("member-row")).toHaveLength(2);
    expect(screen.getByText("bob@demo.gozleme.test")).toBeInTheDocument();
    expect(screen.getByText("2026-11-15 10:05 UTC")).toBeInTheDocument();
    expect(screen.queryByText(/role|admin|owner|promote|transfer/i)).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Remove bob@demo.gozleme.test" }));
    expect(spy).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Confirm remove" }));
    await waitFor(() => expect(router.refresh).toHaveBeenCalled());
    expect(sent(spy)).toMatchObject({ url: `/api/organizations/${ORG}/members/u-bob`, method: "DELETE" });
  });

  it("explains the last-member rule when leaving is refused", async () => {
    respond(409, { error: "last_member" });
    render(<MemberList organizationId={ORG} members={[alice]} />);
    fireEvent.click(screen.getByRole("button", { name: "Leave organization" }));
    fireEvent.click(screen.getByRole("button", { name: "Confirm leave" }));
    expect(await screen.findByTestId("member-error")).toHaveTextContent(LAST_MEMBER);
    expect(router.push).not.toHaveBeenCalled();
  });

  it("returns to the organization list after leaving", async () => {
    respond(204);
    render(<MemberList organizationId={ORG} members={[alice, bob]} />);
    fireEvent.click(screen.getByRole("button", { name: "Leave organization" }));
    fireEvent.click(screen.getByRole("button", { name: "Confirm leave" }));
    await waitFor(() => expect(router.push).toHaveBeenCalledWith("/organizations"));
  });
});

describe("InvitationPanel", () => {
  const open: Invitation[] = [{ id: "i1", createdAt: "2026-11-15T10:00:00Z", expiresAt: "2026-11-22T10:00:00Z", createdBy: "u-alice" }];

  it("shows a new code once in a labelled read-only field and lists open invitations without codes", async () => {
    respond(201, { id: "i2", createdAt: "2026-11-15T11:00:00Z", expiresAt: "2026-11-22T11:00:00Z", createdBy: "u-alice", code: "ABCD-EFGH-JKMN-PQRS-TVWX-YZ01-2345-6789" });
    render(<InvitationPanel organizationId={ORG} invitations={open} members={[alice, bob]} />);
    expect(screen.queryByTestId("invitation-code")).not.toBeInTheDocument();
    expect(screen.getByTestId("invitation-row")).toHaveTextContent("2026-11-22 10:00 UTC");
    fireEvent.click(screen.getByRole("button", { name: "Create invitation" }));
    const code = await screen.findByLabelText(/shown once/);
    expect(code).toHaveValue("ABCD-EFGH-JKMN-PQRS-TVWX-YZ01-2345-6789");
    expect(code).toHaveAttribute("readonly");
  });

  it("revokes by id", async () => {
    const spy = respond(204);
    render(<InvitationPanel organizationId={ORG} invitations={open} members={[alice, bob]} />);
    fireEvent.click(screen.getByRole("button", { name: /^Revoke invitation/ }));
    await waitFor(() => expect(router.refresh).toHaveBeenCalled());
    expect(sent(spy)).toMatchObject({ url: `/api/organizations/${ORG}/invitations/i1`, method: "DELETE" });
  });

  it("names the creator by member email, or as a former member once they have left", () => {
    const { unmount } = render(<InvitationPanel organizationId={ORG} invitations={open} members={[alice, bob]} />);
    expect(screen.getByTestId("invitation-creator")).toHaveTextContent("alice@demo.gozleme.test");
    unmount();
    render(<InvitationPanel organizationId={ORG} invitations={[{ ...open[0]!, createdBy: null }]} members={[bob]} />);
    expect(screen.getByTestId("invitation-creator")).toHaveTextContent("Former member");
  });

  it("says when there are no open invitations", () => {
    render(<InvitationPanel organizationId={ORG} invitations={[]} members={[alice]} />);
    expect(screen.getByTestId("no-invitations")).toBeInTheDocument();
  });
});

describe("RedeemInvitationForm", () => {
  it("joins and opens the organization", async () => {
    const spy = respond(200, { organizationId: ORG, alreadyMember: false });
    render(<RedeemInvitationForm />);
    fireEvent.change(screen.getByLabelText("Invitation code"), { target: { value: "abcd efgh" } });
    fireEvent.click(screen.getByRole("button", { name: "Join organization" }));
    await waitFor(() => expect(router.push).toHaveBeenCalledWith(`/organizations/${ORG}`));
    expect(sent(spy)).toEqual({ url: "/api/invitations/redeem", method: "POST", body: { code: "abcd efgh" } });
  });

  it.each([
    [400, INVALID_INVITATION],
    [429, "Too many attempts. Wait a minute and try again."],
    [503, "The service is unavailable. Try again shortly."],
  ])("shows one message for status %i", async (status, text) => {
    respond(status, { error: "x" });
    render(<RedeemInvitationForm />);
    fireEvent.click(screen.getByRole("button", { name: "Join organization" }));
    expect(await screen.findByTestId("redeem-error")).toHaveTextContent(text);
  });
});

describe("OrganizationsUnavailable", () => {
  it("states the outage without any organization data", () => {
    render(<OrganizationsUnavailable />);
    expect(screen.getByRole("alert")).toHaveTextContent("can't be verified");
  });
});
