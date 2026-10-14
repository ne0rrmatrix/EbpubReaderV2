import { defineConfig, devices } from "@playwright/test";

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
