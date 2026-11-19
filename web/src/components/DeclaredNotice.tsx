import { DECLARED_NOT_VERIFIED } from "@/lib/children";

/** The fixed FR-014 notice on every child view. */
export function DeclaredNotice() {
  return (
    <p className="notice" role="note" data-testid="declared-notice">
      {DECLARED_NOT_VERIFIED}
    </p>
  );
}
