import { FAMILIES, type Family } from "@/lib/manifests";

/** The six capability families (FR-001), each with its native format, as one radio group. */
export function FamilyPicker({ value, onChange }: { value: Family; onChange: (family: Family) => void }) {
  return (
    <fieldset className="family-picker" data-testid="family-picker">
      <legend>Family</legend>
      {FAMILIES.map((f) => (
        <label key={f.id} className="family-option" title={f.format}>
          <input type="radio" name="family" value={f.id} checked={value === f.id} onChange={() => onChange(f.id)} data-testid={`family-${f.id}`} />
          {f.label}
        </label>
      ))}
    </fieldset>
  );
}
