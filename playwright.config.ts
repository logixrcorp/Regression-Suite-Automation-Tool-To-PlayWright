import { defineConfig, devices } from '@playwright/test';
import dotenv from 'dotenv';
import { AuthFile } from './constants/AuthFile';

if (!process.env.CI) {
  dotenv.config({ path: '.env' });
}

/**
 * Which sign-in flow the `setup` project runs. Providing an authenticator
 * secret is what selects the MFA path, so there is no third switch to keep in
 * sync with reality.
 */
const USE_MFA = Boolean(process.env.D365_OTP_SECRET);

/**
 * Generated D365 specs are UI-heavy and stateful: no parallelism within a
 * legal entity, generous timeouts, and a trace on first retry so a failure in
 * a converted recording can be replayed step by step.
 *
 * Sign-in happens once, in the `setup` project, and every generated spec
 * inherits the session through `storageState`.
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
    // Generated specs navigate with relative URLs, so the environment lives
    // here in one place rather than being baked into every recording.
    baseURL: process.env.D365_BASE_URL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    actionTimeout: 60_000,
  },

  projects: [
    {
      name: 'setup',
      testMatch: USE_MFA ? /mfa\.setup\.ts/ : /login\.setup\.ts/,
    },
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        storageState: AuthFile,
      },
      dependencies: ['setup'],
    },
  ],
});
