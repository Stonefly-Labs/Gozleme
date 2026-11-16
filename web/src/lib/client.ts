/** Browser-side call to a same-origin route handler; network failures read as 503. */
export async function send(method: "POST" | "PUT" | "PATCH" | "DELETE", path: string, body?: unknown): Promise<{ status: number; json: unknown }> {
  try {
    const res = await fetch(path, {
      method,
      headers: body === undefined ? undefined : { "content-type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = res.status === 204 ? "" : await res.text();
    let json: unknown = null;
    try {
      json = text ? JSON.parse(text) : null;
    } catch {
      json = null;
    }
    return { status: res.status, json };
  } catch {
    return { status: 503, json: null };
  }
}

export const unavailableText = "The service is unavailable. Try again shortly.";

/** A 403 from an organization action: the caller's roles changed or never permitted it (GOZ-0004). */
export const forbiddenText = "You no longer have permission to do that.";

/**
 * Where to go after a 403: the same organization page with a notice. The navigation re-renders the
 * page with the caller's current permissions, so stale controls disappear while the notice stays.
 */
export const forbiddenNoticePath = (organizationId: string) => `/organizations/${encodeURIComponent(organizationId)}?notice=forbidden`;

/** The API's field error for a display name, if any. */
export function displayNameError(json: unknown): string | null {
  const fields = (json as { fields?: Record<string, string[]> } | null)?.fields;
  return fields?.displayName?.[0] ?? null;
}
