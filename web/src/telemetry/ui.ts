import { SpanStatusCode, trace } from "@opentelemetry/api";
import { BROWSER_SERVICE_NAME } from "./browser";

/**
 * Browser span around a UI identity flow (`gozleme.ui.<flow>`), so its fetch joins one trace from
 * the browser through web, API and database. Only the bounded outcome is recorded — never form values.
 */
export async function traced<T>(flow: "sign_in" | "register" | "sign_out" | "sign_out_all", run: () => Promise<T>): Promise<T> {
  return trace.getTracer(BROWSER_SERVICE_NAME).startActiveSpan(`gozleme.ui.${flow}`, async (span) => {
    try {
      const result = await run();
      span.setAttribute("gozleme.ui.outcome", result ? "success" : "failure");
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
