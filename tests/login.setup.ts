import { test as setup } from '@playwright/test';
import { login } from 'playwright-m365-helpers';
import { AuthFile } from '../constants/AuthFile';
import { hasStoredSession, resolveCredentials } from '../constants/Credentials';

/**
 * Sign in to D365 with a username and password, and save the session.
 *
 * This is the no-MFA path: it needs a test account excluded from MFA, which in
 * practice means a Conditional Access policy scoped to a trusted location. If
 * the account uses an authenticator app, use `mfa.setup.ts` instead by setting
 * D365_OTP_SECRET.
 *
 * https://playwright.dev/docs/auth
 */
setup('authenticate', async ({ page }) => {
  // A session captured by hand is a supported way in, so having one on disk
  // and no credentials configured is a valid state, not a failure.
  setup.skip(
    hasStoredSession() && !process.env.D365_USERNAME,
    `Reusing the existing session in ${AuthFile}.`,
  );

  const { baseUrl, username, password } = resolveCredentials();

  await login(page, baseUrl, username, password);
  await page.context().storageState({ path: AuthFile });
});
