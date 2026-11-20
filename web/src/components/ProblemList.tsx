import { locationText, type Problem } from "@/lib/manifests";

/**
 * Rejection problems (contracts/manifest-validation.md): category, rule, location and the fixed catalogue message
 * only. The API never returns submitted values, and this list renders nothing outside the problem shape. A `publish`
 * list (GOZ-0008 publish-time rules, which validation accepts) does not point back to validation in its truncation note.
 */
export function ProblemList({ problems, truncated, context = "validate" }: { problems: Problem[]; truncated: boolean; context?: "validate" | "publish" }) {
  return (
    <section className="card" aria-labelledby="problems-heading" data-testid="problem-list">
      <h2 id="problems-heading">Rejected: {problems.length === 1 ? "1 problem" : `${problems.length} problems`}</h2>
      <ol className="problem-list">
        {problems.map((p, i) => (
          <li key={i} className="problem" data-testid="problem">
            <p className="problem-meta">
              <span className="badge badge-down" data-testid="problem-category">{p.category}</span>
              <code data-testid="problem-rule">{p.rule}</code>
              <code data-testid="problem-location">{locationText(p.location)}</code>
            </p>
            <p data-testid="problem-message">{p.message}</p>
          </li>
        ))}
      </ol>
      {truncated && (
        <p className="note" data-testid="problems-truncated">
          {context === "publish" ? "More problems exist. Fix these and publish again to see the rest." : "More problems exist. Fix these and validate again to see the rest."}
        </p>
      )}
    </section>
  );
}
