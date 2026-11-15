/** FR-015: a post-sign-in target is honoured only as a same-site relative path; anything else lands on `/`. */
export function safeReturnTo(value: string | null | undefined): string {
  if (!value || !value.startsWith("/") || value.startsWith("//") || value.startsWith("/\\")) return "/";
  if (/[\u0000-\u001f\u007f\\]/.test(value)) return "/";
  try {
    const base = "http://gozleme.invalid";
    const url = new URL(value, base);
    if (url.origin !== base) return "/";
    const path = url.pathname + url.search + url.hash;
    return path.startsWith("//") ? "/" : path;
  } catch {
    return "/";
  }
}
