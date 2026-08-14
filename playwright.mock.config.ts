import { defineConfig } from '@playwright/test';

/**
 * Self-test harness: runs the *generated* specs against a mock that reproduces
 * D365's DOM contract (data-dyn-controlname, blocking overlay, dialog scoping,
 * indexed grid cells). It verifies the converter and the runtime helper, not
 * the customer's environment.
 */
process.env.D365_BASE_URL ||= 'http://127.0.0.1:3999';

export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  workers: 1,
  reporter: [['list']],
  use: {
    trace: 'off',
    screenshot: 'off',
    // Default to the browser `npx playwright install chromium` manages. Set
    // CHROMIUM_PATH only where that download is unavailable (an offline build
    // agent, or a sandbox pinning its own Chromium).
    launchOptions: process.env.CHROMIUM_PATH
      ? { executablePath: process.env.CHROMIUM_PATH }
      : {},
  },
  webServer: {
    command: 'node mock/server.mjs',
    url: 'http://127.0.0.1:3999/',
    // Locally, reuse whatever is already listening. On CI, never: a stale
    // server would silently serve a different mock than this commit's.
    reuseExistingServer: !process.env.CI,
    stdout: 'ignore',
  },
});
