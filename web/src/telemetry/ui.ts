import { SpanStatusCode, trace } from "@opentelemetry/api";
import { BROWSER_SERVICE_NAME } from "./browser";

/** Bounded UI flows: identity (GOZ-0001), organizations (GOZ-0002), roles (GOZ-0004), publishers (GOZ-0005), manifests (GOZ-0006) and revisions (GOZ-0007). */
export type UiFlow =
  | "sign_in"
  | "register"
  | "sign_out"
  | "sign_out_all"
  | "org_create"
  | "org_rename"
  | "org_select"
  | "invite_create"
  | "invite_revoke"
  | "invite_redeem"
  | "member_remove"
  | "member_leave"
  | "member_roles"
  | "publisher_register"
  | "publisher_rename"
  | "capability_create"
  | "capability_rename"
  | "manifest_validate"
  | "manifest_publish";

/**
 * Browser span around a UI flow (`gozleme.ui.<flow>`), so its fetch joins one trace from
 * the browser through web, API and database. Only the bounded outcome is recorded — never form values, names or codes.
 */
export async function traced<T>(
  flow: UiFlow,
  run: () => Promise<T>,
  succeeded: (result: T) => boolean = (result) => !!result,
): Promise<T> {
  return trace.getTracer(BROWSER_SERVICE_NAME).startActiveSpan(`gozleme.ui.${flow}`, async (span) => {
    try {
      const result = await run();
      span.setAttribute("gozleme.ui.outcome", succeeded(result) ? "success" : "failure");
      return result;
    } catch (err) {
      span.setStatus({ code: SpanStatusCode.ERROR });
      span.setAttribute("gozleme.ui.outcome", "error");
      throw err;
    } finally {
      span.end();
    }
  });
}
