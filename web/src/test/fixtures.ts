import type { ServiceStatus } from "@/lib/status";

export const healthy: ServiceStatus = {
  contractVersion: "1",
  service: "gozleme-api",
  version: "0.1.0-test",
  status: "healthy",
  checkedAt: "2026-11-14T12:00:00Z",
  dependencies: [
    {
      name: "database",
      status: "healthy",
      required: true,
      latencyMs: 3.2,
      schemaVersion: "20261114000000_Foundation",
      message: null,
      capabilities: { vector: { available: true, enabled: false } },
    },
  ],
};
