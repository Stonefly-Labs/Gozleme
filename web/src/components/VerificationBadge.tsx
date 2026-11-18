import { NEVER_VERIFIED, reasonMessage, type Verification } from "@/lib/revisions";

/**
 * The verification result, computed by the API from the stored bytes on every read (GOZ-0007 FR-010). A failed
 * revision shows its fixed reason and is never presented as verified (US2-AS4); a retired key is noted.
 */
export function VerificationBadge({ verification, detailed = false }: { verification: Verification; detailed?: boolean }) {
  if (verification.status === "verified") {
    return (
      <span className="verification">
        <span className="badge badge-ok" data-testid="verification-badge" data-status="verified">
          Verified
        </span>
        {verification.key.status === "retired" && (
          <span className="badge badge-warn" data-testid="key-retired-badge">
            {verification.key.retiredReason === "compromised" ? "Key retired: compromised" : "Key retired"}
          </span>
        )}
      </span>
    );
  }

  return (
    <span className="verification">
      <span className="badge badge-down" data-testid="verification-badge" data-status="failed" data-reason={verification.reason ?? ""}>
        Failed
      </span>
      {detailed && (
        <span className="verification-reason" role="alert" data-testid="verification-reason">
          {reasonMessage(verification.reason)} {NEVER_VERIFIED}
        </span>
      )}
    </span>
  );
}
