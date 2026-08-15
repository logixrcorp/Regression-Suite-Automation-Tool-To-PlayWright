import { existsSync } from 'node:fs';
import { AuthFile } from './AuthFile';

export interface Credentials {
  baseUrl: string;
  username: string;
  password: string;
  otpSecret?: string;
}

/** True when a previously captured session is already on disk. */
export const hasStoredSession = (): boolean => existsSync(AuthFile);

/**
 * Read sign-in details from the environment, failing with instructions rather
 * than letting a missing variable surface as an unexplained login timeout.
 *
 * Scripted sign-in is not always possible: a tenant enforcing number matching
 * or push approval cannot be automated at all. Capturing a session by hand is
 * a supported path, not a workaround, which is why the message points at it.
 */
export function resolveCredentials(): Credentials {
  const baseUrl = process.env.D365_BASE_URL;
  const username = process.env.D365_USERNAME;
  const password = process.env.D365_PASSWORD;

  const missing = [
    !baseUrl && 'D365_BASE_URL',
    !username && 'D365_USERNAME',
    !password && 'D365_PASSWORD',
  ].filter(Boolean);

  if (missing.length) {
    throw new Error(
      `Cannot sign in: ${missing.join(', ')} not set.\n\n` +
        `Copy .env.sample to .env and fill it in, or capture a session by hand once:\n` +
        `  npx playwright open --save-storage=${AuthFile} <your D365 URL>\n\n` +
        `Hand capture is the only option on a tenant whose MFA uses number ` +
        `matching or push approval, since neither can be scripted.`,
    );
  }

  return {
    baseUrl: baseUrl!,
    username: username!,
    password: password!,
    otpSecret: process.env.D365_OTP_SECRET,
  };
}
