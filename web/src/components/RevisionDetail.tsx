"use client";

import { useState } from "react";
import { formatUtc } from "@/lib/format";
import { type RevisionDetail as RevisionDetailData, SUSPENDED_PUBLISHER } from "@/lib/revisions";
import { AttestationPanel } from "./AttestationPanel";
import { VerificationBadge } from "./VerificationBadge";

type Tab = "native" | "projection" | "attestation";

const TABS: { id: Tab; label: string }[] = [
  { id: "native", label: "Native" },
  { id: "projection", label: "Projection" },
  { id: "attestation", label: "Attestation" },
];

/**
 * One stored revision (GOZ-0007 User Stories 1 and 3, R17) with tabs Native, Projection and Attestation. Everything
 * shown is the stored record as published: the native bytes, the stored projection (never re-derived) and the
 * attestation with its verification computed now.
 */
export function RevisionDetail({
  organizationId,
  publisherId,
  capabilityId,
  revision,
}: {
  organizationId: string;
  publisherId: string;
  capabilityId: string;
  revision: RevisionDetailData;
}) {
  const [tab, setTab] = useState<Tab>("native");
  const { native, projection } = revision;

  function onKey(event: React.KeyboardEvent<HTMLDivElement>) {
    if (event.key !== "ArrowRight" && event.key !== "ArrowLeft") return;
    const index = TABS.findIndex((t) => t.id === tab);
    const next = TABS[(index + (event.key === "ArrowRight" ? 1 : TABS.length - 1)) % TABS.length]!;
    setTab(next.id);
    document.getElementById(`revision-tab-${next.id}`)?.focus();
  }

  return (
    <div className="stack" data-testid="revision-detail">
      {revision.publisherSuspended && (
        <div className="notice" role="status" data-testid="suspended-publisher-banner">
          <p>{SUSPENDED_PUBLISHER}</p>
        </div>
      )}
      <section className="card" aria-labelledby="revision-heading">
        <h2 id="revision-heading">
          Revision {revision.sequence} · <code data-testid="revision-version">{revision.version}</code>
        </h2>
        <dl>
          <div className="row">
            <dt>Verification</dt>
            <dd>
              <VerificationBadge verification={revision.verification} detailed />
            </dd>
          </div>
          <div className="row">
            <dt>Capability</dt>
            <dd>
              <code className="cell-wrap" data-testid="revision-capability">
                {revision.capabilityId}
              </code>
            </dd>
          </div>
          <div className="row">
            <dt>Publisher</dt>
            <dd>
              <code className="cell-wrap" data-testid="revision-publisher">
                {revision.publisherId}
              </code>
            </dd>
          </div>
          <div className="row">
            <dt>Published</dt>
            <dd>
              <time dateTime={revision.publishedAt}>{formatUtc(revision.publishedAt)}</time>
            </dd>
          </div>
          <div className="row">
            <dt>By account</dt>
            <dd>
              <code data-testid="revision-published-by">{revision.publishedBy}</code>
            </dd>
          </div>
          <div className="row">
            <dt>Validated with</dt>
            <dd>
              <code>{`${revision.adapter.id}/${revision.adapter.version} · rules v${revision.ruleCatalogueVersion} · schemas v${revision.schemaBundleVersion}`}</code>
            </dd>
          </div>
        </dl>
      </section>
      <div className="tabs" role="tablist" aria-label="Revision parts" onKeyDown={onKey}>
        {TABS.map((t) => (
          <button
            key={t.id}
            id={`revision-tab-${t.id}`}
            type="button"
            role="tab"
            className="tab"
            aria-selected={tab === t.id}
            aria-controls={`revision-panel-${t.id}`}
            tabIndex={tab === t.id ? 0 : -1}
            onClick={() => setTab(t.id)}
            data-testid={`tab-${t.id}`}
          >
            {t.label}
          </button>
        ))}
      </div>
      <div id={`revision-panel-${tab}`} role="tabpanel" aria-labelledby={`revision-tab-${tab}`}>
        {tab === "native" && (
          <section className="card" aria-labelledby="native-heading" data-testid="panel-native">
            <div className="panel-head">
              <h2 id="native-heading">Native document</h2>
              <span className="panel-version">{`${native.format} ${native.formatVersion}`}</span>
            </div>
            <dl>
              <div className="row">
                <dt>Digest</dt>
                <dd>
                  <code className="cell-wrap" data-testid="native-digest">
                    {native.digest}
                  </code>
                </dd>
              </div>
              <div className="row">
                <dt>Size</dt>
                <dd>
                  {native.byteLength.toLocaleString("en")} bytes ({native.encoding}, as published)
                </dd>
              </div>
            </dl>
            <pre className="native-document" tabIndex={0} aria-label="Native document text" data-testid="native-document">
              {native.document}
            </pre>
          </section>
        )}
        {tab === "projection" && (
          <section className="card" aria-labelledby="projection-heading" data-testid="panel-projection">
            <div className="panel-head">
              <h2 id="projection-heading">Projection</h2>
              <span className="panel-version" data-testid="projection-version">{`projection v${projection.projectionVersion} · ${projection.adapter}`}</span>
            </div>
            <dl>
              <div className="row">
                <dt>Digest</dt>
                <dd>
                  <code className="cell-wrap" data-testid="projection-digest">
                    {revision.projectionDigest}
                  </code>
                </dd>
              </div>
              <div className="row">
                <dt>Kind</dt>
                <dd>
                  <code>{projection.kind}</code>
                </dd>
              </div>
              <div className="row">
                <dt>Native name</dt>
                <dd>
                  <code className="cell-wrap">{projection.nativeName}</code>
                </dd>
              </div>
              <div className="row">
                <dt>Title</dt>
                <dd>{projection.title}</dd>
              </div>
              <div className="row">
                <dt>Declared version</dt>
                <dd>
                  <code>{projection.declaredVersion}</code>
                </dd>
              </div>
            </dl>
            <p className="field-hint">The projection stored at publication, never derived again.</p>
            <pre className="native-document" tabIndex={0} aria-label="Stored projection" data-testid="projection-json">
              {JSON.stringify(projection, null, 2)}
            </pre>
          </section>
        )}
        {tab === "attestation" && <AttestationPanel organizationId={organizationId} publisherId={publisherId} capabilityId={capabilityId} revision={revision} />}
      </div>
    </div>
  );
}
