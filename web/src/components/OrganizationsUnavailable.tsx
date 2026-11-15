/** Honest degraded view (FR-022): the session or organization data cannot be verified, so none is shown. */
export function OrganizationsUnavailable() {
  return (
    <div className="notice" role="alert" data-testid="organizations-unavailable">
      <p>The service is unavailable, so your session and organizations can&apos;t be verified. Try again shortly.</p>
    </div>
  );
}
