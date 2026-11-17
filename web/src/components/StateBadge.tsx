import type { PublisherState } from "@/lib/publishers";

/** Active, or suspended after an operator reclaim (FR-034); derived by the API from the handle claim. */
export function StateBadge({ state }: { state: PublisherState }) {
  return state === "suspended" ? (
    <span className="badge badge-down" data-testid="suspended-badge">
      Suspended
    </span>
  ) : (
    <span className="badge badge-ok">Active</span>
  );
}
