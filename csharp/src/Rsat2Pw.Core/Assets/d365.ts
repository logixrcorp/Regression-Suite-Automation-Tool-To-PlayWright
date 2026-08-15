/**
 * D365 Finance & Operations Playwright runtime.
 *
 * Generated specs call only into this class. That is deliberate: the D365 web
 * client is aggressively asynchronous, and every wait/retry quirk is
 * concentrated here instead of being duplicated across generated code. When
 * the client changes, you fix one file rather than regenerating everything.
 *
 * The core insight that makes this work at all: Task Recorder records AOT
 * control names, and the D365 client renders those same names into the DOM as
 * `data-dyn-controlname`. So recorded control identity maps to a stable
 * locator with no heuristics.
 */

import { expect, type Locator, type Page } from '@playwright/test';

/** Overlays the client shows while it is busy. Tune per environment/version. */
const BLOCKING_SELECTORS = [
  '.blockUI',
  '.sysBlockingProgress',
  '.dyn-modalOverlay',
  '[data-dyn-role="Blocking"]',
  '#ProcessingScreen',
];

/** System commands render with reserved control names, not user-defined ones. */
const SYSTEM_COMMANDS: Record<string, string> = {
  save: 'SystemDefinedSaveButton',
  new: 'SystemDefinedNewButton',
  delete: 'SystemDefinedDeleteButton',
  edit: 'SystemDefinedEditButton',
  refresh: 'SystemDefinedRefreshButton',
  close: 'SystemDefinedCloseButton',
  ok: 'OkButton',
  cancel: 'CancelButton',
  yes: 'Yes',
  no: 'No',
};

export interface D365Options {
  /**
   * e.g. https://<env>.operations.dynamics.com
   *
   * Normally left unset: Playwright's `baseURL` supplies it from the config,
   * so the environment is configured in one place instead of being baked into
   * every generated spec.
   */
  baseUrl?: string;
  /** D365 company / legal entity, appended as the `cmp` query parameter. */
  company?: string;
  /** How long to wait for the client to stop blocking, in ms. */
  idleTimeout?: number;
}

/**
 * Hosts D365 hands off to when there is no valid session. Landing on one of
 * these means the stored session is missing or expired.
 */
const SIGN_IN_HOSTS = [
  'login.microsoftonline.com',
  'login.microsoft.com',
  'login.windows.net',
  'adfs',
];

export class D365 {
  /** Innermost active scope: the page, or a dialog/form subtree. */
  private scopes: Locator[] = [];

  private constructor(
    readonly page: Page,
    private readonly options: Required<D365Options>,
  ) {}

  static async open(page: Page, options: D365Options = {}): Promise<D365> {
    const resolved: Required<D365Options> = {
      // Empty is the normal case: the URL then stays relative and Playwright
      // resolves it against the `baseURL` in the config.
      baseUrl: options.baseUrl ?? '',
      company: options.company ?? process.env.D365_COMPANY ?? 'USMF',
      idleTimeout: options.idleTimeout ?? 60_000,
    };

    const d365 = new D365(page, resolved);
    await d365.goto(`cmp=${encodeURIComponent(resolved.company)}`);
    await d365.waitForIdle();
    return d365;
  }

  /** Build a URL, relative unless an explicit base was supplied. */
  private url(query: string): string {
    return this.options.baseUrl ? `${this.options.baseUrl}/?${query}` : `/?${query}`;
  }

  private async goto(query: string): Promise<void> {
    try {
      await this.page.goto(this.url(query));
    } catch (error) {
      // A relative URL with no baseURL configured fails deep inside Playwright
      // with "Invalid URL", which says nothing about what to fix.
      if (!this.options.baseUrl && /invalid url/i.test(String(error))) {
        throw new Error(
          'No D365 environment configured. Set D365_BASE_URL in .env (it becomes ' +
            "Playwright's baseURL), or pass baseUrl to D365.open().",
        );
      }
      throw error;
    }

    await this.assertSignedIn();
  }

  /**
   * Fail immediately when the client bounced us to sign-in.
   *
   * Without this the run continues against the identity provider's page and
   * dies much later on a control lookup, reporting a missing D365 form rather
   * than the expired session that actually caused it.
   */
  private async assertSignedIn(): Promise<void> {
    const current = this.page.url();

    if (!SIGN_IN_HOSTS.some((host) => current.includes(host))) {
      return;
    }

    throw new Error(
      `Not signed in - D365 redirected to ${current}\n\n` +
        'The saved session is missing or expired. Refresh it with:\n' +
        '  npx playwright test --project=setup\n\n' +
        'If sign-in cannot be scripted on this tenant, capture one by hand:\n' +
        '  npx playwright open --save-storage=playwright/.auth/user.json <D365 URL>',
    );
  }

  // -- scoping --------------------------------------------------------------

  /** Current search root. Dialogs push a narrower scope onto the stack. */
  private get scope(): Locator | Page {
    return this.scopes.length ? this.scopes[this.scopes.length - 1] : this.page;
  }

  /** Locate a recorded control by its AOT name. */
  ctl(controlName: string): Locator {
    return this.scope.locator(`[data-dyn-controlname="${controlName}"]`).first();
  }

  // -- waiting --------------------------------------------------------------

  /**
   * Wait until the client is genuinely idle. Element visibility alone is not
   * enough in D365: controls render before they are interactive, and the
   * blocking overlay is what actually gates input.
   */
  /**
   * Note the visibility test rather than an existence test: D365 keeps its
   * blocking overlay in the DOM permanently and toggles it, so `querySelector`
   * alone would report "blocked" forever.
   */
  private async isBlocked(): Promise<boolean> {
    return this.page.evaluate(
      (selectors) =>
        selectors.some((selector) =>
          // querySelectorAll, not querySelector: the client renders more than
          // one overlay of a given class, and a hidden first match would
          // otherwise mask a visible later one and report "idle" while the
          // client is still blocking.
          Array.from(document.querySelectorAll(selector)).some((el) => {
            const style = window.getComputedStyle(el);
            if (style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0') {
              return false;
            }
            const rect = (el as HTMLElement).getBoundingClientRect();
            return rect.width > 0 && rect.height > 0;
          }),
        ),
      BLOCKING_SELECTORS,
    );
  }

  async waitForIdle(): Promise<void> {
    const deadline = Date.now() + this.options.idleTimeout;

    await this.page.waitForLoadState('domcontentloaded');

    while (Date.now() < deadline) {
      if (!(await this.isBlocked())) {
        // Short quiet period: the client often blocks again a tick later.
        await this.page.waitForTimeout(150);
        if (!(await this.isBlocked())) return;
      }
      await this.page.waitForTimeout(100);
    }

    throw new Error(`D365 client still blocked after ${this.options.idleTimeout}ms`);
  }

  // -- navigation -----------------------------------------------------------

  /**
   * Deep-link to a menu item rather than replaying recorded navigation-pane
   * clicks, which are slow and break whenever the menu structure moves.
   */
  async navigate(menuItem: string, kind: 'Display' | 'Action' | 'Output' = 'Display'): Promise<void> {
    // Display menu items deep-link by bare name; action menu items need the
    // `action:` prefix (`?mi=action:SysEntityNavigation` is the documented
    // form). Output menu items are treated as bare here - that case is not
    // verified against a live environment, so check it before relying on it.
    const prefix = kind === 'Action' ? 'action:' : '';
    await this.goto(
      `mi=${prefix}${encodeURIComponent(menuItem)}&cmp=${encodeURIComponent(this.options.company)}`,
    );
    await this.waitForIdle();
  }

  /** Assert the expected form is the active one before acting on its controls. */
  async enterForm(formName: string): Promise<void> {
    await expect(this.page.locator(`[data-dyn-form-name="${formName}"]`).first())
      .toBeVisible({ timeout: this.options.idleTimeout });
    await this.waitForIdle();
  }

  async leaveForm(_formName: string): Promise<void> {
    await this.command('Close');
    await this.waitForIdle();
  }

  // -- interaction ----------------------------------------------------------

  async setField(controlName: string, value: string): Promise<void> {
    const control = this.ctl(controlName);
    await control.waitFor({ state: 'visible' });

    // The recorded control name lands on a wrapper; the editable node is
    // usually a descendant input.
    const input = control.locator('input, textarea').first();
    const target = (await input.count()) ? input : control;

    const type = await target.getAttribute('type');
    if (type === 'checkbox') {
      const checked = await target.isChecked();
      const want = /^(true|yes|1|checked)$/i.test(value);
      if (checked !== want) await target.click();
    } else {
      await target.click();
      await target.fill('');
      await target.fill(value);
      // Commit the edit; D365 validates on blur, not on keystroke.
      await target.press('Tab');
    }

    await this.waitForIdle();
  }

  /** Choose a value through a lookup rather than typing it. */
  async lookup(controlName: string, value: string): Promise<void> {
    const control = this.ctl(controlName);
    await control.click();
    await control.locator('input').first().fill(value);
    await this.waitForIdle();

    const flyout = this.page.locator('.lookupPopup, [role="listbox"]').first();
    await flyout.waitFor({ state: 'visible' });
    await flyout.getByText(value, { exact: true }).first().click();
    await this.waitForIdle();
  }

  async click(controlName: string): Promise<void> {
    await this.ctl(controlName).click();
    await this.waitForIdle();
  }

  async command(name: string): Promise<void> {
    const mapped = SYSTEM_COMMANDS[name.toLowerCase()];
    const candidates = [mapped, name].filter(Boolean) as string[];

    for (const candidate of candidates) {
      const locator = this.ctl(candidate);
      if (await locator.count()) {
        await locator.click();
        await this.waitForIdle();
        return;
      }
    }

    // Last resort: a button labelled with the command text. Scoped, not
    // page-wide - a dialog's "OK" must never fall back to the one behind it.
    const byLabel = this.scope.getByRole('button', { name, exact: false }).first();
    await byLabel.click();
    await this.waitForIdle();
  }

  // -- grids ----------------------------------------------------------------

  /**
   * Grids are virtualized: only rendered rows exist in the DOM, so a recorded
   * row index cannot be used as a raw nth() into the page. We scroll the grid
   * until the requested row materialises.
   */
  async setGridCell(gridName: string, columnName: string, rowIndex: number, value: string): Promise<void> {
    const grid = this.ctl(gridName);
    await grid.waitFor({ state: 'visible' });

    const cell = grid.locator(
      `[data-dyn-controlname="${columnName}"][data-dyn-row-index="${rowIndex}"]`,
    ).first();

    for (let attempt = 0; attempt < 20 && !(await cell.count()); attempt++) {
      await grid.press('PageDown').catch(() => undefined);
      await this.waitForIdle();
    }

    if (!(await cell.count())) {
      throw new Error(`Grid '${gridName}' never rendered row ${rowIndex} for column '${columnName}'`);
    }

    await cell.click();
    const input = cell.locator('input, textarea').first();
    await input.fill(value);
    await input.press('Tab');
    await this.waitForIdle();
  }

  // -- dialogs --------------------------------------------------------------

  /** Run `body` with control lookups scoped to a dialog or slider overlay. */
  async withDialog(_name: string, body: () => Promise<void>): Promise<void> {
    const dialog = this.page
      .locator('[role="dialog"], .dialog-popup, .sliderContainer')
      .last();
    await dialog.waitFor({ state: 'visible' });

    this.scopes.push(dialog);
    try {
      await body();
    } finally {
      this.scopes.pop();
    }
    await this.waitForIdle();
  }

  // -- assertions -----------------------------------------------------------

  async expectValue(controlName: string, expected: string): Promise<void> {
    const control = this.ctl(controlName);
    const input = control.locator('input, textarea').first();

    if (await input.count()) {
      await expect(input).toHaveValue(expected);
    } else {
      await expect(control).toHaveText(expected);
    }
  }

  async expectVisible(controlName: string): Promise<void> {
    await expect(this.ctl(controlName)).toBeVisible();
  }
}
