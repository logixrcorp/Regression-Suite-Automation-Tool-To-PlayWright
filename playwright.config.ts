import { defineConfig } from '@playwright/test';

/**
 * Generated D365 specs are UI-heavy and stateful: no parallelism within a
 * legal entity, generous timeouts, and a trace on first retry so a failure in
 * a converted recording can be replayed step by step.
 */
export default defineConfig({
  testDir: './tests',
  timeout: 5 * 60 * 1000,
  expect: { timeout: 30_000 },
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: process.env.D365_BASE_URL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    actionTimeout: 60_000,
    // D365 sign-in is federated; reuse a saved session instead of scripting it.
    storageState: process.env.D365_STORAGE_STATE || undefined,
  },
});
