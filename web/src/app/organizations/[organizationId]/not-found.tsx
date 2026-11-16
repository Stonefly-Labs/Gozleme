import Link from "next/link";
/** The single answer for non-members, unknown ids and malformed ids (FR-006); HTTP 404. */
export default function OrganizationNotFound() {
  return (
    <main className="shell shell-narrow">
      <title>Organization not found — Gözleme</title>
      <header className="header">
        <div>
          <p className="eyebrow">Gözleme · AI capability registry</p>
          <h1>Organization not found</h1>
        </div>
      </header>
      <div className="card">
        <p>This organization doesn&apos;t exist, or you&apos;re not a member of it.</p>
        <p className="form-alt">
          <Link href="/organizations">Back to your organizations</Link>
        </p>
      </div>
    </main>
  );
}
