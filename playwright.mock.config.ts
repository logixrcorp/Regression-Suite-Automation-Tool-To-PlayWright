import { defineConfig } from '@playwright/test';

/**
 * Self-test harness: runs the *generated* specs against a mock that reproduces
 * D365's DOM contract (data-dyn-controlname, blocking overlay, dialog scoping,
 * indexed grid cells). It verifies the converter and the runtime helper, not
 * the customer's environment.
 */
const MOCK_URL = process.env.D365_BASE_URL || 'http://127.0.0.1:3999';

export default defineConfig({
  // Both the generated specs and the runtime helper's own tests. The latter
  // live under mock/ so that the real-environment config, which only looks at
  // tests/, never tries to run them against a live instance.
  testDir: '.',
  testMatch: ['tests/*.spec.ts', 'mock/*.spec.ts'],
  // The mock has no identity provider, so the sign-in setup project must not
  // run here. testMatch already excludes *.setup.ts; this is explicit so it
  // stays that way.
  testIgnore: /.*\.setup\.ts/,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  workers: 1,
  reporter: [['list']],
  use: {
    // Same contract as the real config: generated specs navigate relatively,
    // and the environment is supplied here. The mock needs no storageState.
    baseURL: MOCK_URL,
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
