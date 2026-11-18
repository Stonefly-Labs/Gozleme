import Link from "next/link";

/** The single answer for non-members and unknown, malformed, foreign or misplaced revision ids; HTTP 404. */
export default function RevisionNotFound() {
  return (
    <main className="shell shell-narrow">
      <title>Revision not found — Gözleme</title>
      <header className="header">
        <div>
          <p className="eyebrow">Gözleme · AI capability registry</p>
          <h1>Revision not found</h1>
        </div>
      </header>
      <div className="card">
        <p>This revision doesn&apos;t exist, or you&apos;re not a member of its organization.</p>
        <p className="form-alt">
          <Link href="/organizations">Back to your organizations</Link>
        </p>
      </div>
    </main>
  );
}
