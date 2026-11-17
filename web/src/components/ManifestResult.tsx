import type { Projection, ValidationResult } from "@/lib/manifests";
import { ProblemList } from "./ProblemList";

const Derived = ({ projection, pointer }: { projection: Projection; pointer: string }) =>
  projection.derived.includes(pointer) ? (
    <span className="badge badge-unknown derived" title="Derived by Gözleme, not declared in the native document" data-testid="derived-marker">
      derived
    </span>
  ) : null;

const text = (value: unknown) => (Array.isArray(value) ? value.join(", ") : typeof value === "string" ? value : JSON.stringify(value));

/**
 * An accepted manifest as three separate parts (FR-003, User Story 3): the envelope, the native document exactly as
 * submitted with its digest, and the derived projection. Each panel is labelled with its own version; derived
 * projection values are marked. A rejection renders the problem list.
 */
export function ManifestResult({ result }: { result: ValidationResult }) {
  if (result.outcome === "rejected") return <ProblemList problems={result.problems} truncated={result.truncated} />;

  const { envelope, native, projection } = result;
  return (
    <div className="panels" data-testid="manifest-accepted">
      <p className="badge badge-ok" role="status" data-testid="manifest-outcome">
        Accepted
      </p>
      <section className="card" aria-labelledby="envelope-heading" data-testid="panel-envelope">
        <div className="panel-head">
          <h2 id="envelope-heading">Envelope</h2>
          <span className="panel-version">envelope v{envelope.envelopeVersion}</span>
        </div>
        <dl>
          <div className="row"><dt>Family</dt><dd><code>{envelope.family}</code></dd></div>
          <div className="row"><dt>Capability</dt><dd><code className="cell-wrap">{envelope.capability}</code></dd></div>
          <div className="row"><dt>Version</dt><dd><code>{envelope.version}</code></dd></div>
          {envelope.title && <div className="row"><dt>Title</dt><dd>{envelope.title}</dd></div>}
          <div className="row"><dt>Native</dt><dd><code>{`${envelope.native.format} ${envelope.native.formatVersion} (${envelope.native.encoding})`}</code></dd></div>
        </dl>
      </section>
      <section className="card" aria-labelledby="native-heading" data-testid="panel-native">
        <div className="panel-head">
          <h2 id="native-heading">Native document</h2>
          <span className="panel-version" data-testid="native-version">{`${native.format} ${native.formatVersion}`}</span>
        </div>
        <dl>
          <div className="row"><dt>Digest</dt><dd><code className="cell-wrap" data-testid="native-digest">{native.digest}</code></dd></div>
          <div className="row"><dt>Size</dt><dd>{native.byteLength.toLocaleString("en")} bytes ({native.encoding}, unchanged)</dd></div>
        </dl>
        <pre className="native-document" tabIndex={0} aria-label="Native document text" data-testid="native-document">{native.document}</pre>
      </section>
      <section className="card" aria-labelledby="projection-heading" data-testid="panel-projection">
        <div className="panel-head">
          <h2 id="projection-heading">Projection</h2>
          <span className="panel-version" data-testid="projection-version">{`projection v${projection.projectionVersion} · ${projection.adapter}`}</span>
        </div>
        <dl>
          <div className="row"><dt>Kind</dt><dd><code>{projection.kind}</code></dd></div>
          <div className="row"><dt>Native name</dt><dd><code className="cell-wrap">{projection.nativeName}</code></dd></div>
          <div className="row"><dt>Title</dt><dd>{projection.title}<Derived projection={projection} pointer="/title" /></dd></div>
          <div className="row"><dt>Description</dt><dd>{projection.description}</dd></div>
          <div className="row"><dt>Declared version</dt><dd><code>{projection.declaredVersion}</code></dd></div>
          {Object.entries(projection.attributes).map(([name, value]) => (
            <div className="row" key={name} data-testid={`attribute-${name}`}>
              <dt>{name}</dt>
              <dd><code>{text(value)}</code><Derived projection={projection} pointer={`/attributes/${name}`} /></dd>
            </div>
          ))}
        </dl>
        <h3>Entries ({projection.entries.length})</h3>
        {projection.entries.length === 0 ? (
          <p className="muted">None.</p>
        ) : (
          <table className="table" data-testid="projection-entries">
            <thead><tr><th>Kind</th><th>Native name</th><th>Title</th></tr></thead>
            <tbody>
              {projection.entries.map((e, i) => (
                <tr key={i}>
                  <td>{e.kind}</td>
                  <td className="cell-wrap"><code>{e.nativeName}</code><Derived projection={projection} pointer={`/entries/${i}/nativeName`} /></td>
                  <td>{e.title ?? "—"}<Derived projection={projection} pointer={`/entries/${i}/title`} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}
