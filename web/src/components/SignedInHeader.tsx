import Link from "next/link";
import { OrganizationSwitcher } from "@/components/OrganizationSwitcher";
import { listOrganizations } from "@/lib/organizations";

/**
 * Site banner for signed-in pages (GOZ-0002): the organization switcher and links to the
 * organization pages. It renders no organization data unless the API returned it.
 */
export async function SignedInHeader() {
  const list = await listOrganizations();
  return (
    <header className="site-header">
      <nav aria-label="Primary" className="site-nav">
        <Link href="/organizations">Organizations</Link>
        <Link href="/join">Join</Link>
      </nav>
      {list.state === "ok" && (
        <OrganizationSwitcher organizations={list.data.organizations} activeOrganizationId={list.data.activeOrganizationId} />
      )}
    </header>
  );
}
