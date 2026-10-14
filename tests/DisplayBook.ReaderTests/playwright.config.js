import { defineConfig, devices } from "@playwright/test";

// Before every action and assertion, Playwright waits for any pending navigation of the page to
// finish. The reader sends each bridge message as a navigation that never commits, and when one
// replaces another before its request starts, Chromium never sends that request: the navigation
// stays pending and the test waits until it times out (under load, as on CI runners). Every
// navigation after the shell has loaded is a bridge message, so skip that wait. (Set here, before
// the workers start, so they inherit it.)
process.env.PLAYWRIGHT_SKIP_NAVIGATION_CHECK ??= "1";

// Chromium only: it is the engine behind the Windows reader (WebView2) and Android's WebView,
// and the only one Playwright can collect JavaScript coverage from.
export default defineConfig({
    testDir: "./tests",
    fullyParallel: true,
    forbidOnly: Boolean(process.env.CI),
    retries: process.env.CI ? 1 : 0,
    reporter: [["list"]],
    globalSetup: "./tests/support/coverage-setup.js",
    globalTeardown: "./tests/support/coverage-teardown.js",
    use: {
        ...devices["Desktop Chrome"],
        viewport: { width: 1000, height: 700 }
    },
    projects: [{ name: "chromium" }]
});
