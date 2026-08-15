import { test as setup } from '@playwright/test';
import { login } from 'playwright-m365-helpers';
import { AuthFile } from '../constants/AuthFile';
import { hasStoredSession, resolveCredentials } from '../constants/Credentials';

/**
 * Sign in to D365 with a username, password and a time-based one-time code,
 * then save the session.
 *
 * Selected automatically when D365_OTP_SECRET is set. That secret is the seed
 * shown as "Can't scan the image?" when enrolling the authenticator app, so
 * the code can be computed rather than read off a phone. It only works for
 * TOTP: number matching and push approval cannot be automated, and those
 * tenants need a hand-captured session instead.
 *
 * https://playwright.dev/docs/auth
 */
setup('authenticate', async ({ page }) => {
  setup.skip(
    hasStoredSession() && !process.env.D365_USERNAME,
    `Reusing the existing session in ${AuthFile}.`,
  );

  const { baseUrl, username, password, otpSecret } = resolveCredentials();

  await login(page, baseUrl, username, password, otpSecret);
  await page.context().storageState({ path: AuthFile });
});
