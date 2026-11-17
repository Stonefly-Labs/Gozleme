"use client";

import { useState } from "react";

/** A permanent identifier in monospace with a copy button (GOZ-0005 research R13). */
export function CanonicalId({ value, label, testId }: { value: string; label: string; testId?: string }) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  }

  return (
    <span className="canonical-id">
      <code data-testid={testId}>{value}</code>
      <button type="button" className="button button-small" onClick={copy} aria-label={`Copy ${label}`}>
        {copied ? "Copied" : "Copy"}
      </button>
    </span>
  );
}
