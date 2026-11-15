import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AccountBar } from "./AccountBar";
import { RegisterForm } from "./RegisterForm";
import { SignInForm } from "./SignInForm";

const router = { replace: vi.fn(), refresh: vi.fn() };
vi.mock("next/navigation", () => ({ useRouter: () => router }));

function respond(status: number, body?: unknown) {
  return vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(body === undefined ? null : JSON.stringify(body), { status }));
}

function fill(label: RegExp, value: string) {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

describe("SignInForm", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => vi.restoreAllMocks());

  it("has labelled fields, offers registration only when open, and never an external sign-in option", () => {
    const { rerender } = render(<SignInForm returnTo="/" registrationEnabled />);
    expect(screen.getByLabelText("Email")).toHaveAttribute("autocomplete", "username");
    expect(screen.getByLabelText("Password")).toHaveAttribute("type", "password");
    expect(screen.getByRole("link", { name: "Create an account" })).toHaveAttribute("href", "/register");
    expect(screen.queryByText(/single sign-on|sso|google|github|microsoft|oidc/i)).not.toBeInTheDocument();
    rerender(<SignInForm returnTo="/" registrationEnabled={false} />);
    expect(screen.queryByRole("link", { name: "Create an account" })).not.toBeInTheDocument();
  });

  it("navigates to the safe return path on success", async () => {
    const fetchSpy = respond(200, {});
    render(<SignInForm returnTo="/somewhere" registrationEnabled />);
    fill(/^email$/i, "alice@demo.gozleme.test");
    fill(/^password$/i, "local-dev-demo-pass");
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    await waitFor(() => expect(router.replace).toHaveBeenCalledWith("/somewhere"));
    expect(JSON.parse((fetchSpy.mock.calls[0]![1] as RequestInit).body as string)).toEqual({ email: "alice@demo.gozleme.test", password: "local-dev-demo-pass" });
  });

  it.each([
    [401, "Email or password is incorrect."],
    [429, "Too many attempts. Wait a minute and try again."],
    [503, "The service is unavailable. Try again shortly."],
  ])("shows a generic, announced message for %i", async (status, text) => {
    respond(status, { error: "x" });
    render(<SignInForm returnTo="/" registrationEnabled />);
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(text);
    expect(router.replace).not.toHaveBeenCalled();
  });
});

describe("RegisterForm", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => vi.restoreAllMocks());

  it("validates length and confirmation locally before calling the API", async () => {
    const fetchSpy = respond(201, {});
    render(<RegisterForm />);
    fill(/^email$/i, "new@example.test");
    fill(/^password$/i, "short");
    fill(/confirm password/i, "different");
    fireEvent.click(screen.getByRole("button", { name: "Create account" }));
    expect(await screen.findByTestId("password-error")).toHaveTextContent("Use at least 12 characters.");
    expect(screen.getByTestId("confirm-error")).toHaveTextContent("The passwords don't match.");
    expect(screen.getByLabelText(/^password$/i)).toHaveAttribute("aria-invalid", "true");
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("shows server field errors and the generic duplicate message", async () => {
    respond(400, { error: "validation_failed", fields: { email: ["Enter a valid email address."] } });
    render(<RegisterForm />);
    fill(/^password$/i, "a-long-passphrase");
    fill(/confirm password/i, "a-long-passphrase");
    fireEvent.click(screen.getByRole("button", { name: "Create account" }));
    expect(await screen.findByTestId("email-error")).toHaveTextContent("Enter a valid email address.");

    vi.restoreAllMocks();
    respond(409, { error: "registration_failed" });
    fireEvent.click(screen.getByRole("button", { name: "Create account" }));
    expect(await screen.findByTestId("form-error")).toHaveTextContent("We couldn't create an account with these details.");
  });

  it("switches to the closed state when the operator closed registration", async () => {
    respond(403, { error: "registration_closed" });
    render(<RegisterForm />);
    fill(/^password$/i, "a-long-passphrase");
    fill(/confirm password/i, "a-long-passphrase");
    fireEvent.click(screen.getByRole("button", { name: "Create account" }));
    expect(await screen.findByTestId("registration-closed")).toHaveTextContent("Registration is closed");
  });

  it("goes to the dashboard after registering", async () => {
    respond(201, {});
    render(<RegisterForm />);
    fill(/^email$/i, "new@example.test");
    fill(/^password$/i, "a-long-passphrase");
    fill(/confirm password/i, "a-long-passphrase");
    fireEvent.click(screen.getByRole("button", { name: "Create account" }));
    await waitFor(() => expect(router.replace).toHaveBeenCalledWith("/"));
  });
});

describe("AccountBar", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => vi.restoreAllMocks());

  it.each([
    ["Sign out", "/api/auth/sign-out"],
    ["Sign out everywhere", "/api/auth/sign-out-all"],
  ])("%s ends the session server-side and returns to sign-in", async (button, path) => {
    const fetchSpy = respond(204);
    render(<AccountBar email="alice@demo.gozleme.test" />);
    expect(screen.getByTestId("account-email")).toHaveTextContent("alice@demo.gozleme.test");
    fireEvent.click(screen.getByRole("button", { name: button }));
    await waitFor(() => expect(router.replace).toHaveBeenCalledWith("/login"));
    expect(fetchSpy.mock.calls[0]![0]).toBe(path);
    expect((fetchSpy.mock.calls[0]![1] as RequestInit).method).toBe("POST");
  });

  it("reports a failed sign-out and stays put", async () => {
    respond(503, { error: "unavailable" });
    render(<AccountBar email="alice@demo.gozleme.test" />);
    fireEvent.click(screen.getByRole("button", { name: "Sign out" }));
    expect(await screen.findByText("Couldn't sign out. Try again.")).toBeInTheDocument();
    expect(router.replace).not.toHaveBeenCalled();
  });
});
