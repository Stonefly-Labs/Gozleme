import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { BANNED, CANONICAL_PUB, CAP, detail, KEY, KEY_ID, ORG, ORIGIN, PUB, REV } from "@/lib/revisions.fixtures";
import { COMPROMISED_KEY, proofWording, SUSPENDED_PUBLISHER, VERIFICATION_MESSAGES } from "@/lib/revisions";
import { AttestationPanel } from "./AttestationPanel";
import { RevisionDetail } from "./RevisionDetail";

const PROOF =
  `Signed by registry ${ORIGIN} for publisher ${CANONICAL_PUB} with a registry-held key. ` +
  "This shows that this registry accepted the revision from an authorized publisher member. It is not a signature made by the publisher.";

const renderPanel = (revision = detail()) => render(<AttestationPanel organizationId={ORG} publisherId={PUB} capabilityId={CAP} revision={revision} />);
const renderDetail = (revision = detail()) => render(<RevisionDetail organizationId={ORG} publisherId={PUB} capabilityId={CAP} revision={revision} />);

const retired = (reason: "lost" | "compromised") =>
  detail({ verification: { status: "verified", reason: null, key: { id: KEY, keyId: KEY_ID, status: "retired", retiredAt: "2026-11-18T11:00:00Z", retiredReason: reason } } });

describe("AttestationPanel (GOZ-0007 US3, US4)", () => {
  it("shows the statement fields, key ID, algorithm, statement version, result and the fixed proof wording", () => {
    renderPanel();
    expect(proofWording(ORIGIN, CANONICAL_PUB)).toBe(PROOF);
    expect(screen.getByTestId("attestation-proof").textContent).toBe(PROOF);
    expect(screen.getByTestId("attestation-statement-version")).toHaveTextContent("statement v1");
    expect(screen.getByTestId("attestation-algorithm")).toHaveTextContent("Ed25519");
    expect(screen.getByTestId("attestation-key-id")).toHaveTextContent(KEY_ID);
    expect(screen.getByTestId("verification-badge")).toHaveTextContent("Verified");
    expect(screen.getByTestId("statement-native.digest")).toHaveTextContent(/sha256:a{64}/);
    expect(screen.getByTestId("statement-publisher")).toHaveTextContent(CANONICAL_PUB);
    expect(screen.getByTestId("statement-key.id")).toHaveTextContent(KEY_ID);
    expect(screen.getByTestId("key-status")).toHaveTextContent("Active");
    expect(screen.queryByTestId("compromised-banner")).not.toBeInTheDocument();
  });

  it("links the four downloads and the public key through the web routes", () => {
    renderPanel();
    const base = `/api/organizations/${ORG}/publishers/${PUB}/capabilities/${CAP}/revisions/${REV}`;
    for (const part of ["native", "projection", "statement", "signature"]) expect(screen.getByTestId(`download-${part}`)).toHaveAttribute("href", `${base}/${part}`);
    expect(screen.getByTestId("download-public-key")).toHaveAttribute("href", `/api/organizations/${ORG}/publishers/${PUB}/signing-keys/${KEY}/public-key`);
  });

  it("shows a retired key with its time and reason, and a prominent banner for compromised", () => {
    renderPanel(retired("lost"));
    expect(screen.getByTestId("key-status")).toHaveTextContent("Retired");
    expect(screen.getByTestId("key-retired-reason")).toHaveTextContent("lost");
    expect(screen.getByTestId("key-status")).toHaveTextContent("2026-11-18 11:00 UTC");
    expect(screen.queryByTestId("compromised-banner")).not.toBeInTheDocument();
  });

  it("banners a compromised key", () => {
    renderPanel(retired("compromised"));
    expect(screen.getByTestId("compromised-banner")).toHaveTextContent(COMPROMISED_KEY);
    expect(screen.getByTestId("verification-badge")).toHaveTextContent("Verified");
  });

  it("shows a failed result with its reason and never as verified (T048)", () => {
    renderPanel(detail({ verification: { status: "failed", reason: "signature_invalid", key: { id: KEY, keyId: KEY_ID, status: "active" } } }));
    expect(screen.getByTestId("verification-badge")).toHaveTextContent("Failed");
    expect(screen.getByTestId("verification-reason")).toHaveTextContent(VERIFICATION_MESSAGES.signature_invalid);
    expect(screen.queryByText("Verified")).not.toBeInTheDocument();
  });

  it("says when the stored statement could not be read", () => {
    const tampered = detail();
    tampered.attestation = { ...tampered.attestation, statement: {} };
    tampered.verification = { status: "failed", reason: "malformed_statement", key: tampered.verification.key };
    renderPanel(tampered);
    expect(screen.getByTestId("statement-unreadable")).toBeInTheDocument();
  });

  it("renders the API's proof text, which is the fixed R17 wording", () => {
    const revision = detail();
    expect(revision.attestation.proof).toBe(PROOF);
    renderPanel({ ...revision, attestation: { ...revision.attestation, proof: "text from the API" } });
    expect(screen.getByTestId("attestation-proof").textContent).toBe("text from the API");
  });

  // T061: the wording guard for every state the panel and detail can render.
  it("contains none of the banned phrases and contains the fixed wording in every state", () => {
    for (const revision of [detail(), retired("lost"), retired("compromised"), detail({ publisherSuspended: true })]) {
      const { container, unmount } = renderPanel(revision);
      const text = container.textContent!.toLowerCase();
      for (const banned of BANNED) expect(text).not.toContain(banned);
      expect(container.textContent).toContain(PROOF);
      unmount();
    }
  });
});

describe("RevisionDetail (GOZ-0007 US1, US3)", () => {
  it("has Native, Projection and Attestation tabs, starting on the stored native document", () => {
    renderDetail();
    expect(screen.getAllByRole("tab").map((t) => t.textContent)).toEqual(["Native", "Projection", "Attestation"]);
    expect(screen.getByTestId("native-document").textContent).toBe('{"name":"io.example/weather"}\n');
    fireEvent.click(screen.getByTestId("tab-projection"));
    expect(screen.getByTestId("projection-version")).toHaveTextContent("projection v1 · mcp/1");
    expect(screen.getByTestId("projection-digest")).toHaveTextContent(/sha256:c{64}/);
    expect(screen.queryByTestId("native-document")).not.toBeInTheDocument();
    fireEvent.click(screen.getByTestId("tab-attestation"));
    expect(screen.getByTestId("tab-attestation")).toHaveAttribute("aria-selected", "true");
    expect(screen.getByTestId("attestation-proof")).toHaveTextContent(PROOF);
  });

  it("moves between tabs with the arrow keys", () => {
    renderDetail();
    fireEvent.keyDown(screen.getByRole("tablist"), { key: "ArrowLeft" });
    expect(screen.getByTestId("tab-attestation")).toHaveAttribute("aria-selected", "true");
    fireEvent.keyDown(screen.getByRole("tablist"), { key: "ArrowRight" });
    expect(screen.getByTestId("tab-native")).toHaveAttribute("aria-selected", "true");
  });

  it("names canonical IDs only and banners a suspended publisher", () => {
    renderDetail(detail({ publisherSuspended: true }));
    expect(screen.getByTestId("suspended-publisher-banner")).toHaveTextContent(SUSPENDED_PUBLISHER);
    expect(screen.getByTestId("revision-publisher")).toHaveTextContent(CANONICAL_PUB);
    expect(screen.getByTestId("revision-capability")).toHaveTextContent(/^gz:\/\//);
  });

  it("contains none of the banned phrases on any tab", () => {
    const { container } = renderDetail(retired("compromised"));
    for (const tab of ["native", "projection", "attestation"]) {
      fireEvent.click(screen.getByTestId(`tab-${tab}`));
      for (const banned of BANNED) expect(container.textContent!.toLowerCase()).not.toContain(banned);
    }
  });
});
