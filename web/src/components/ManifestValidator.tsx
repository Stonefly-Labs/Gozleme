"use client";

import { useState } from "react";
import { errorMessage, type Family, postManifest, resultOf, sampleText, samplesFor, type ValidationResult, validatePath } from "@/lib/manifests";
import { traced } from "@/telemetry/ui";
import { FamilyPicker } from "./FamilyPicker";
import { ManifestResult } from "./ManifestResult";
import { PublishRevision } from "./PublishRevision";

/**
 * Validate a manifest against one capability (GOZ-0006 User Story 1): pick a family, load a bundled sample (its
 * `{{capability}}` replaced with this capability's canonical ID) or paste an envelope, and press **Validate**. The
 * text is sent exactly as typed; the API decides everything and stores nothing. After an accepted result, members
 * holding `manifest.publish` can publish exactly the validated text as a signed revision (GOZ-0007).
 */
export function ManifestValidator({
  organizationId,
  publisherId,
  capabilityId,
  canonicalId,
  canPublish = false,
}: {
  organizationId: string;
  publisherId: string;
  capabilityId: string;
  canonicalId: string;
  canPublish?: boolean;
}) {
  const [family, setFamily] = useState<Family>("mcp");
  const [sampleId, setSampleId] = useState(samplesFor("mcp")[0]!.id);
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<ValidationResult | null>(null);
  // The text that produced `result`: publication posts exactly what was validated, not later edits.
  const [validated, setValidated] = useState("");
  const [error, setError] = useState<string | null>(null);
  const options = samplesFor(family);

  function pick(next: Family) {
    setFamily(next);
    setSampleId(samplesFor(next)[0]!.id);
  }

  function load() {
    const sample = options.find((s) => s.id === sampleId);
    if (!sample) return;
    setText(sampleText(sample, canonicalId));
    setResult(null);
    setError(null);
  }

  async function validate(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    setResult(null);
    const { status, json } = await traced(
      "manifest_validate",
      () => postManifest(validatePath(organizationId, publisherId, capabilityId), text),
      (r) => resultOf(r.status, r.json) !== null,
    );
    setBusy(false);
    const result = resultOf(status, json);
    setValidated(text);
    if (result) setResult(result);
    else setError(errorMessage(status, json));
  }

  return (
    <div className="stack">
      <form className="card form" onSubmit={validate} aria-labelledby="validate-heading" data-testid="manifest-form">
        <h2 id="validate-heading">Validate a manifest</h2>
        <FamilyPicker value={family} onChange={pick} />
        <div className="field">
          <label htmlFor="manifest-sample">Sample</label>
          <div className="form-actions">
            <select id="manifest-sample" value={sampleId} onChange={(e) => setSampleId(e.target.value)} data-testid="sample-select">
              {options.map((s) => (
                <option key={s.id} value={s.id}>
                  {`${s.valid ? "Valid" : "Invalid"}: ${s.title}`}
                </option>
              ))}
            </select>
            <button type="button" className="button" onClick={load} data-testid="load-sample">
              Load sample
            </button>
          </div>
        </div>
        <div className="field">
          <label htmlFor="manifest-text">Manifest envelope (JSON)</label>
          <textarea
            id="manifest-text"
            className="manifest-input"
            value={text}
            onChange={(e) => setText(e.target.value)}
            spellCheck={false}
            data-testid="manifest-text"
          />
          <p className="field-hint">Nothing is stored. The native document is checked against its pinned schema and returned unchanged.</p>
        </div>
        <div className="form-actions">
          <button type="submit" className="button button-primary" disabled={busy || text.length === 0} data-testid="validate">
            {busy ? "Validating…" : "Validate"}
          </button>
        </div>
        {error && (
          <p role="alert" className="form-error" data-testid="manifest-error">
            {error}
          </p>
        )}
      </form>
      <div data-testid="manifest-result" aria-live="polite">
        {result && <ManifestResult result={result} />}
      </div>
      {canPublish && result?.outcome === "accepted" && validated === text && (
        <PublishRevision organizationId={organizationId} publisherId={publisherId} capabilityId={capabilityId} body={validated} canPublish={canPublish} />
      )}
    </div>
  );
}
