"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { type Problem, postManifest } from "@/lib/manifests";
import { createdRevision, publishErrorMessage, revisionPage, revisionsPage, revisionsPath } from "@/lib/revisions";
import { traced } from "@/telemetry/ui";
import { ProblemList } from "./ProblemList";

/**
 * **Publish revision** (GOZ-0007 User Story 1): shown after an accepted validation, and only to members holding
 * `manifest.publish`. It posts exactly the body that was validated; on `201` it opens the new revision. A
 * `version_exists` refusal points to the **Revisions** list, where clients recover by listing. A publish-time rejection
 * (GOZ-0008 `publish.duplicate_entry`, which validation accepts) shows its problem list.
 */
export function PublishRevision({
  organizationId,
  publisherId,
  capabilityId,
  body,
  canPublish,
}: {
  organizationId: string;
  publisherId: string;
  capabilityId: string;
  body: string;
  canPublish: boolean;
}) {
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<{ message: string; listLink: boolean; problems: Problem[] | null; truncated: boolean } | null>(null);
  if (!canPublish) return null;

  async function publish() {
    setBusy(true);
    setError(null);
    const { status, json } = await traced(
      "manifest_publish",
      () => postManifest(revisionsPath(organizationId, publisherId, capabilityId), body),
      (r) => r.status === 201,
    );
    const created = createdRevision(status, json);
    if (created) {
      router.push(revisionPage(organizationId, publisherId, capabilityId, created.id));
      return;
    }

    setBusy(false);
    const code = (json as { error?: unknown } | null)?.error;
    const rejected = json as { outcome?: unknown; problems?: unknown; truncated?: unknown } | null;
    const problems = rejected?.outcome === "rejected" && Array.isArray(rejected.problems) && rejected.problems.length > 0 ? (rejected.problems as Problem[]) : null;
    setError({ message: publishErrorMessage(status, json), listLink: status === 409 && code === "version_exists", problems, truncated: rejected?.truncated === true });
  }

  return (
    <section className="card" aria-labelledby="publish-heading" data-testid="publish-revision">
      <h2 id="publish-heading">Publish revision</h2>
      <p className="field-hint">
        Stores this exact manifest as a new immutable revision, signed by this registry. A published version can never be changed or replaced.
      </p>
      <div className="form-actions">
        <button type="button" className="button button-primary" onClick={publish} disabled={busy} data-testid="publish">
          {busy ? "Publishing…" : "Publish revision"}
        </button>
      </div>
      {error && (
        <p role="alert" className="form-error" data-testid="publish-error">
          {error.message}
          {error.listLink && (
            <>
              {" "}
              <Link href={revisionsPage(organizationId, publisherId, capabilityId)} data-testid="publish-revisions-link">
                See the revisions
              </Link>
            </>
          )}
        </p>
      )}
      {error?.problems && <ProblemList problems={error.problems} truncated={error.truncated} />}
    </section>
  );
}
