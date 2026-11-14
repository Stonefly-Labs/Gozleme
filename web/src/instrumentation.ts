import { registerOTel } from "@vercel/otel";

/**
 * Foundational OpenTelemetry for the web tier (GOZ-0000-IF-11). Server-side spans for routes and
 * outgoing fetches; W3C trace context is propagated to the Gözleme API so a dashboard refresh is one
 * distributed trace. Export uses OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set; otherwise spans are
 * created but not exported.
 */
export function register() {
  const apiUrl = process.env.GOZLEME_API_URL ?? "http://localhost:8080";
  registerOTel({
    serviceName: "gozleme-web",
    attributes: {
      "service.version": process.env.GOZLEME_BUILD ?? "0.0.0",
      "deployment.environment.name": process.env.NODE_ENV ?? "development",
    },
    instrumentationConfig: {
      fetch: {
        // Only our own API receives trace headers; never leak context to third parties.
        propagateContextUrls: [new RegExp(`^${apiUrl.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}`)],
      },
    },
  });
}
