"use client";

import { useEffect } from "react";
import { startBrowserTelemetry } from "./browser";

/** Mount once in the root layout to start browser tracing. Renders nothing. */
export function BrowserTelemetry() {
  useEffect(() => {
    startBrowserTelemetry();
  }, []);
  return null;
}
