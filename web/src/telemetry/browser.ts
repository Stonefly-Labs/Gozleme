import { OTLPTraceExporter } from "@opentelemetry/exporter-trace-otlp-http";
import { registerInstrumentations } from "@opentelemetry/instrumentation";
import { DocumentLoadInstrumentation } from "@opentelemetry/instrumentation-document-load";
import { FetchInstrumentation } from "@opentelemetry/instrumentation-fetch";
import { resourceFromAttributes } from "@opentelemetry/resources";
import { BatchSpanProcessor, WebTracerProvider } from "@opentelemetry/sdk-trace-web";

/** Same-origin ingestion route; the collector itself is never exposed to browsers. */
export const BROWSER_OTLP_PATH = "/api/otel/v1/traces";
export const BROWSER_SERVICE_NAME = "gozleme-web-browser";

let started = false;

/**
 * Browser telemetry (GOZ-0000-IF-11): page-load and fetch spans. Same-origin fetches carry W3C
 * `traceparent`, so a dashboard refresh is one trace from the browser through web, API and database.
 */
export function startBrowserTelemetry() {
  if (started || typeof window === "undefined") return;
  started = true;

  const provider = new WebTracerProvider({
    resource: resourceFromAttributes({ "service.name": BROWSER_SERVICE_NAME }),
    spanProcessors: [
      new BatchSpanProcessor(new OTLPTraceExporter({ url: new URL(BROWSER_OTLP_PATH, window.location.origin).href }), {
        scheduledDelayMillis: 1000,
      }),
    ],
  });
  provider.register();

  registerInstrumentations({
    instrumentations: [
      new DocumentLoadInstrumentation(),
      new FetchInstrumentation({
        // Never trace the exporter's own uploads (would loop) and only propagate to our own origin.
        ignoreUrls: [new RegExp(`${BROWSER_OTLP_PATH}$`)],
        clearTimingResources: true,
      }),
    ],
  });
}
