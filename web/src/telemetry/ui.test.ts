import { trace } from "@opentelemetry/api";
import { BasicTracerProvider, InMemorySpanExporter, SimpleSpanProcessor } from "@opentelemetry/sdk-trace-base";
import { afterAll, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { traced } from "./ui";

const exporter = new InMemorySpanExporter();

beforeAll(() => {
  trace.setGlobalTracerProvider(new BasicTracerProvider({ spanProcessors: [new SimpleSpanProcessor(exporter)] }));
});
afterAll(() => trace.disable());
beforeEach(() => exporter.reset());

const outcome = () => exporter.getFinishedSpans().map((s) => [s.name, s.attributes["gozleme.ui.outcome"]]);

describe("traced", () => {
  it("records failure when the predicate rejects a truthy result such as a 401 status", async () => {
    await traced("sign_in", async () => 401, (s) => s === 200);
    await traced("sign_in", async () => 200, (s) => s === 200);
    expect(outcome()).toEqual([
      ["gozleme.ui.sign_in", "failure"],
      ["gozleme.ui.sign_in", "success"],
    ]);
  });

  it("defaults to truthiness and marks thrown errors", async () => {
    await traced("sign_out", async () => false);
    await expect(traced("sign_out_all", async () => Promise.reject(new Error("boom")))).rejects.toThrow("boom");
    expect(outcome()).toEqual([
      ["gozleme.ui.sign_out", "failure"],
      ["gozleme.ui.sign_out_all", "error"],
    ]);
  });
});
