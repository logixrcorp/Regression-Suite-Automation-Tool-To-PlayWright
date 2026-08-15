/**
 * Where the signed-in browser session is stored. The `setup` project writes it
 * once and every test project reuses it, so sign-in happens a single time per
 * run rather than per test.
 *
 * Git-ignored: it holds live session cookies.
 */
export const AuthFile = 'playwright/.auth/user.json';
