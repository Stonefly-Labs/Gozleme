import { afterEach, describe, expect, it, vi } from "vitest";
import { POST as redeem } from "../invitations/redeem/route";
import { PATCH as rename, GET as getOne } from "./[organizationId]/route";
import { DELETE as revoke } from "./[organizationId]/invitations/[invitationId]/route";
import { GET as listInvitations, POST as invite } from "./[organizationId]/invitations/route";
import { PUT as setRoles } from "./[organizationId]/members/[userId]/roles/route";
import { DELETE as remove } from "./[organizationId]/members/[userId]/route";
import { GET as members } from "./[organizationId]/members/route";
import { POST as select } from "./[organizationId]/select/route";
import { GET as list, POST as create } from "./route";

const request = (method: string, headers: Record<string, string> = {}) =>
  new Request("http://localhost:3000/api/organizations/x", {
    method,
    body: method === "GET" ? undefined : "{}",
    headers: { "content-type": "application/json", ...headers },
  });

const ORG = "0193a000-0000-7000-8000-000000000002";
const org = { params: Promise.resolve({ organizationId: ORG }) };
const orgUser = { params: Promise.resolve({ organizationId: ORG, userId: "u1" }) };
const orgInvitation = { params: Promise.resolve({ organizationId: ORG, invitationId: "i1" }) };

type Handler = (req: Request, ctx: never) => Promise<Response>;
const unsafe: [string, string, Handler, unknown][] = [
  ["POST /api/organizations", "POST", create as Handler, undefined],
  ["PATCH /api/organizations/{id}", "PATCH", rename as Handler, org],
  ["POST …/select", "POST", select as Handler, org],
  ["POST …/invitations", "POST", invite as Handler, org],
  ["DELETE …/invitations/{id}", "DELETE", revoke as Handler, orgInvitation],
  ["DELETE …/members/{id}", "DELETE", remove as Handler, orgUser],
  ["PUT …/members/{id}/roles", "PUT", setRoles as Handler, orgUser],
  ["POST /api/invitations/redeem", "POST", redeem as Handler, undefined],
];

describe("organization route handlers (CSRF layer 1, GOZ-0001-IF-01)", () => {
  afterEach(() => vi.restoreAllMocks());

  it.each(unsafe)("%s refuses cross-site requests without calling the API", async (_name, method, handler, ctx) => {
    const upstream = vi.spyOn(globalThis, "fetch");
    const res = await handler(request(method, { "sec-fetch-site": "cross-site" }), ctx as never);
    expect(res.status).toBe(403);
    expect(upstream).not.toHaveBeenCalled();
  });

  it.each(unsafe)("%s refuses requests with neither fetch metadata nor origin", async (_name, method, handler, ctx) => {
    const upstream = vi.spyOn(globalThis, "fetch");
    const res = await handler(request(method), ctx as never);
    expect(res.status).toBe(403);
    expect(upstream).not.toHaveBeenCalled();
  });

  it.each([
    ["POST", create as Handler, undefined, /\/api\/v1\/organizations$/],
    ["PATCH", rename as Handler, org, new RegExp(`/api/v1/organizations/${ORG}$`)],
    ["POST", select as Handler, org, new RegExp(`/api/v1/organizations/${ORG}/select$`)],
    ["POST", invite as Handler, org, new RegExp(`/api/v1/organizations/${ORG}/invitations$`)],
    ["DELETE", revoke as Handler, orgInvitation, new RegExp(`/api/v1/organizations/${ORG}/invitations/i1$`)],
    ["DELETE", remove as Handler, orgUser, new RegExp(`/api/v1/organizations/${ORG}/members/u1$`)],
    ["PUT", setRoles as Handler, orgUser, new RegExp(`/api/v1/organizations/${ORG}/members/u1/roles$`)],
    ["POST", redeem as Handler, undefined, /\/api\/v1\/invitations\/redeem$/],
  ])("same-origin %s forwards to the matching API path", async (method, handler, ctx, path) => {
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 204 }));
    const res = await handler(request(method, { "sec-fetch-site": "same-origin" }), ctx as never);
    expect(res.status).toBe(204);
    const [url, init] = upstream.mock.calls[0]! as [URL, RequestInit];
    expect(String(url)).toMatch(path);
    expect(init.method).toBe(method);
  });

  it.each([
    ["GET list", list as Handler, undefined, /\/api\/v1\/organizations$/],
    ["GET one", getOne as Handler, org, new RegExp(`/api/v1/organizations/${ORG}$`)],
    ["GET members", members as Handler, org, new RegExp(`/members$`)],
    ["GET invitations", listInvitations as Handler, org, new RegExp(`/invitations$`)],
  ])("%s forwards without an origin check and relays the status", async (_name, handler, ctx, path) => {
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 404 }));
    const res = await handler(request("GET"), ctx as never);
    expect(res.status).toBe(404);
    expect(String(upstream.mock.calls[0]![0])).toMatch(path);
  });

  it("encodes path segments so an id can never escape its route", async () => {
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 404 }));
    await (getOne as Handler)(request("GET"), { params: Promise.resolve({ organizationId: "../../auth/sign-out-all" }) } as never);
    expect(String(upstream.mock.calls[0]![0])).toMatch(/\/api\/v1\/organizations\/\.\.%2F\.\.%2Fauth%2Fsign-out-all$/);
  });

  it.each([".", ".."])("never lets the dot segment %s retarget the API call", async (id) => {
    const upstream = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 404 }));
    await (remove as Handler)(request("DELETE", { "sec-fetch-site": "same-origin" }), { params: Promise.resolve({ organizationId: ORG, userId: id }) } as never);
    expect(String(upstream.mock.calls[0]![0])).toMatch(new RegExp(`/api/v1/organizations/${ORG}/members/not-found$`));
  });

  it("maps API outages to a generic 503", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response('{"error":"unavailable"}', { status: 503 }));
    const res = await (list as Handler)(request("GET"), undefined as never);
    expect(res.status).toBe(503);
  });
});
