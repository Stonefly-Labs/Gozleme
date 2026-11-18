"use client";

import { useState } from "react";
import { shortDigest } from "@/lib/revisions";

/** A digest shortened for reading, with a button that copies the whole value (GOZ-0007 R17). */
export function ShortDigest({ value, label, testId }: { value: string; label: string; testId?: string }) {
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
      <code title={value} data-testid={testId} data-value={value}>
        {shortDigest(value)}
      </code>
      <button type="button" className="button button-small" onClick={copy} aria-label={`Copy ${label}`}>
        {copied ? "Copied" : "Copy"}
      </button>
    </span>
  );
}
