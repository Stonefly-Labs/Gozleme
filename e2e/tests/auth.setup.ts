import { expect, test as setup } from "@playwright/test";
import { AUTH_STATE, DEMO, DEMO_PASSWORD } from "./evidence";

/** One real sign-in through the UI as the seeded synthetic account; later suites reuse the session. */
setup("sign in as the seeded demo account", async ({ page }) => {
  await page.goto("/login");
  await page.getByLabel("Email").fill(DEMO.alice);
  await page.getByLabel("Password").fill(DEMO_PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByTestId("account-email")).toHaveText(DEMO.alice);
  await page.context().storageState({ path: AUTH_STATE });
});
