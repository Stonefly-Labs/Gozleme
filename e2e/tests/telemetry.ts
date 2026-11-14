/** Helpers for asserting OpenTelemetry output of the running stack (Jaeger v3 query API, Prometheus). */
export const JAEGER_URL = process.env.GOZLEME_JAEGER_URL ?? "http://localhost:16686";
export const METRICS_URL = process.env.GOZLEME_METRICS_URL ?? "http://localhost:8889/metrics";

export interface TraceSpan {
  traceId: string;
  service: string;
  name: string;
  attributes: Record<string, unknown>;
}

type AnyValue = { stringValue?: string; intValue?: string; boolValue?: boolean; doubleValue?: number };
type Attr = { key: string; value: AnyValue };

const flat = (attrs: Attr[] = []) =>
  Object.fromEntries(attrs.map((a) => [a.key, a.value.stringValue ?? a.value.intValue ?? a.value.boolValue ?? a.value.doubleValue]));

/** All spans (any service) of traces that include `service`, within the last few minutes. */
export async function recentSpans(service: string, minutes = 10): Promise<TraceSpan[]> {
  const max = new Date();
  const min = new Date(max.getTime() - minutes * 60_000);
  const url = new URL("/api/v3/traces", JAEGER_URL);
  url.searchParams.set("query.service_name", service);
  url.searchParams.set("query.start_time_min", min.toISOString());
  url.searchParams.set("query.start_time_max", max.toISOString());
  url.searchParams.set("query.num_traces", "50");
  const res = await fetch(url);
  if (!res.ok) return [];
  const body = (await res.json()) as {
    result?: { resourceSpans?: { resource: { attributes: Attr[] }; scopeSpans: { spans: { traceId: string; name: string; attributes?: Attr[] }[] }[] }[] };
  };
  return (body.result?.resourceSpans ?? []).flatMap((rs) => {
    const svc = String(flat(rs.resource.attributes)["service.name"]);
    return rs.scopeSpans.flatMap((ss) => ss.spans.map((s) => ({ traceId: s.traceId, service: svc, name: s.name, attributes: flat(s.attributes) })));
  });
}

/** Groups spans by trace id → set of services present in that trace. */
export function servicesByTrace(spans: TraceSpan[]) {
  const map = new Map<string, Set<string>>();
  for (const s of spans) map.set(s.traceId, (map.get(s.traceId) ?? new Set()).add(s.service));
  return map;
}
