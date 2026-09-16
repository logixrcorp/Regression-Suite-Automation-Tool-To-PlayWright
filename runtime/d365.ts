/**
 * D365 Finance & Operations Playwright runtime.
 *
 * Generated specs call only into this class. That is deliberate: the D365 web
 * client is aggressively asynchronous, and every wait/retry quirk is
 * concentrated here instead of being duplicated across generated code. When
 * the client changes, you fix one file rather than regenerating everything.
 *
 * The core insight that makes this work at all: Task Recorder records AOT
 * control names, and the D365 client renders those same names into the DOM.
 * So recorded control identity maps to a stable locator with no heuristics.
 *
 * What it does *not* render them as is a single attribute. The same control
 * name shows up as `name`, as `data-dyn-controlname`, and on different
 * elements depending on what kind of control it is - which is why the
 * recorder's `ControlType` is carried through to every call here, and why
 * `SELECTORS` below is a table of candidates per type rather than one
 * hard-coded string.
 *
 * ── Verify this table first ──────────────────────────────────────────────
 * The selector table and the grid row convention are the parts most likely to
 * need adjusting for your platform version. They are grounded in how the
 * client renders today, not in a contract Microsoft publishes. When a step
 * fails, the error names the control, its type and every selector that was
 * tried, so the fix is a one-line addition here.
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

/**
 * How each recorded `ControlType` is found in the DOM, in preference order.
 * `{name}` is the recorded control name.
 *
 * Order matters: `name` is the most specific and lands on the interactive
 * element itself, while `data-dyn-controlname` often lands on a wrapper.
 */
const SELECTORS: Record<string, string[]> = {
  button: [
    'button[name="{name}"]',
    'button[data-dyn-controlname="{name}"]',
    '[data-dyn-controlname="{name}"] button',
    // An action-pane command that does not fit collapses into an overflow
    // menu, which the client renders outside the form that owns it.
    '.overflow-menu button[name="{name}"]',
    '[class*="overflow-menu"] button[name="{name}"]',
    '[data-dyn-controlname="{name}"]',
  ],
  input: [
    'input[name="{name}"]',
    'textarea[name="{name}"]',
    '[data-dyn-controlname="{name}"] input',
    '[data-dyn-controlname="{name}"] textarea',
    '[data-dyn-controlname="{name}"]',
  ],
  checkbox: [
    'input[type="checkbox"][name="{name}"]',
    '[data-dyn-controlname="{name}"] input[type="checkbox"]',
    '[data-dyn-controlname="{name}"] .toggle-box',
    // A styled checkbox is a span carrying the control name in its id, not an
    // input carrying it in a name attribute.
    'span[id*="{name}"].toggle-box',
    'span[id*="{name}"].checkBox',
    '[data-dyn-controlname="{name}"]',
  ],
  tab: [
    '[data-dyn-controlname="{name}"] button',
    'button[name="{name}"]',
    '[data-dyn-controlname="{name}"]',
    'li[data-dyn-controlname="{name}"]',
  ],
  grid: [
    '[data-dyn-controlname="{name}"]',
    '[role="grid"][data-dyn-controlname="{name}"]',
    '[data-dyn-controlname="{name}"] [role="grid"]',
  ],
  tree: [
    '[data-dyn-controlname="{name}"]',
    '[role="tree"][data-dyn-controlname="{name}"]',
    '[data-dyn-controlname="{name}"] [role="tree"]',
    '[id*="{name}"][role="tree"]',
  ],
  listbox: [
    '[data-dyn-controlname="{name}"] ul',
    'ul[id*="{name}"]',
    'ul[aria-labelledby*="{name}"]',
    '[data-dyn-controlname="{name}"]',
  ],
  // Anything the recorder gave no type for, or a type not listed below.
  generic: [
    '[data-dyn-controlname="{name}"]',
    '[name="{name}"]',
    '[id$="{name}"]',
  ],
};

/**
 * Recorded `ControlType` -> which selector family finds it.
 *
 * A type that is missing here falls through to `generic`, which still works
 * for most controls - this table is about reaching the *interactive* element
 * rather than the wrapper around it.
 */
const CONTROL_FAMILY: Record<string, keyof typeof SELECTORS> = {
  button: 'button',
  commandbutton: 'button',
  menuitembutton: 'button',
  menubutton: 'button',
  menuitem: 'button',
  dropdialogbutton: 'button',
  togglebutton: 'button',
  anchorbutton: 'button',
  input: 'input',
  real: 'input',
  integer: 'input',
  date: 'input',
  datetime: 'input',
  time: 'input',
  string: 'input',
  multilineinput: 'input',
  segmentedentry: 'input',
  referencegroup: 'input',
  combobox: 'input',
  quickfilter: 'input',
  filtermanager: 'input',
  checkbox: 'checkbox',
  radiobutton: 'checkbox',
  appbartab: 'tab',
  pivotitem: 'tab',
  sectionpage: 'tab',
  tab: 'tab',
  grid: 'grid',
  reactgrid: 'grid',
  tree: 'tree',
  listbox: 'listbox',
};

/**
 * Grid rows. The client virtualizes them, so only rendered rows exist, and the
 * row element carries a 1-based `aria-rowindex` while the recorder counts from
 * zero - hence `row + 1`.
 *
 * This one convention is corroborated: another D365 automation project, run
 * against a live environment, addresses rows the same way.
 */
const GRID_ROW_SELECTORS = (row: number): string[] => [
  `[aria-rowindex="${row + 1}"]`,
  `[data-dyn-row-index="${row}"]`,
  `[role="row"][aria-rowindex="${row + 1}"]`,
];

/** The row the client currently considers active. */
const ACTIVE_ROW_SELECTORS = [
  '[aria-selected="true"]',
  '.dyn-activeRow',
  '[class*="fixedDataTableRowLayout_"][class*="active"]',
];

/**
 * Named client shortcuts, and the control each one is equivalent to. The
 * recorder stores the *name* of the shortcut the user pressed, not the keys,
 * and the reserved control is a much more reliable target than a key combo the
 * browser may swallow.
 */
const SHORTCUT_CONTROLS: Record<string, string[]> = {
  viewedit: ['SystemDefinedEditButton', 'SystemDefinedViewEditButton'],
  save: ['SystemDefinedSaveButton'],
  new: ['SystemDefinedNewButton'],
  delete: ['SystemDefinedDeleteButton'],
  refresh: ['SystemDefinedRefreshButton'],
};

/** How a node in a tree is rendered. */
const TREE_NODE_SELECTOR = '[role="treeitem"], .treeNode, li';

/** Where a lookup renders. It is a form in its own right, outside the caller. */
const LOOKUP_SELECTORS = [
  '.lookupPopup',
  '[role="listbox"]',
  '.sysPopup',
  '[data-dyn-form-name$="Lookup"]',
];

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
  /** Innermost active scope: the page, or a form subtree. */
  private scopes: Locator[] = [];

  /**
   * The row `selectRow()` last moved to, per grid. The recorder emits "move
   * the cursor" and "act on the current row" as separate actions, so the
   * second needs to know what the first chose.
   */
  private cursor = new Map<string, number>();

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

  // -- locating -------------------------------------------------------------

  /** Current search root. Form scopes push a narrower one onto the stack. */
  private get scope(): Locator | Page {
    return this.scopes.length ? this.scopes[this.scopes.length - 1] : this.page;
  }

  private candidates(controlName: string, controlType: string): string[] {
    const family = CONTROL_FAMILY[controlType.toLowerCase()] ?? 'generic';
    const selectors = [...SELECTORS[family]];

    // Always keep the generic forms as a tail: `ControlType` is recorded from
    // the AOT metadata and a control can still render as something else.
    for (const generic of SELECTORS.generic) {
      if (!selectors.includes(generic)) selectors.push(generic);
    }

    return selectors.map((s) => s.replaceAll('{name}', controlName));
  }

  /**
   * Resolve a recorded control to something visible on screen.
   *
   * Visibility is part of the search, not a check afterwards: the client keeps
   * whole form subtrees in the DOM after you leave them, so the first match
   * for a control name is regularly a hidden copy on a form nobody is looking
   * at.
   */
  async locate(controlName: string, controlType = ''): Promise<Locator> {
    const selectors = this.candidates(controlName, controlType);
    const roots: (Locator | Page)[] = [this.scope];
    // A form scope narrows the search, but the client renders flyouts, lookups
    // and overflow menus outside the form that opened them, so the page stays
    // as a fallback rather than a hard boundary.
    if (this.scope !== this.page) roots.push(this.page);

    for (const root of roots) {
      for (const selector of selectors) {
        const locator = root.locator(selector).filter({ visible: true }).first();
        if (await locator.count()) return locator;
      }
    }

    throw new Error(
      `Control '${controlName}'${controlType ? ` (${controlType})` : ''} was not found.\n\n` +
        'Tried:\n' +
        selectors.map((s) => `  ${s}`).join('\n') +
        '\n\nIf this control renders differently on your platform version, add the ' +
        'selector to SELECTORS in runtime/d365.ts - every generated spec picks it up.',
    );
  }

  // -- waiting --------------------------------------------------------------

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

  /**
   * Wait until the client is genuinely idle. Element visibility alone is not
   * enough in D365: controls render before they are interactive, and the
   * blocking overlay is what actually gates input.
   */
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

  /**
   * Run `body` with control lookups scoped to a form.
   *
   * The recorder attributes actions to whatever form held focus, and that is
   * not always the form a control lives on - so a scope that cannot be found
   * is not an error, it just leaves the search where it was. Scoping here buys
   * the common case: a dialog's OK button resolving to the dialog's, not to
   * the one on the form behind it.
   */
  async withForm(formName: string, body: () => Promise<void>): Promise<void> {
    await this.waitForIdle();

    const form = this.page
      .locator(`[data-dyn-form-name="${formName}"]`)
      .filter({ visible: true })
      .last();

    const scoped = (await form.count()) > 0;
    if (scoped) this.scopes.push(form);

    try {
      await body();
    } finally {
      if (scoped) this.scopes.pop();
    }
  }

  /** Assert the expected form is the active one before acting on its controls. */
  async enterForm(formName: string): Promise<void> {
    await expect(this.page.locator(`[data-dyn-form-name="${formName}"]`).first())
      .toBeVisible({ timeout: this.options.idleTimeout });
    await this.waitForIdle();
  }

  /** `CommandName=RequestClose` - close the form or dialog on top. */
  async closeForm(): Promise<void> {
    for (const name of ['SystemDefinedCloseButton', 'CancelButton', 'CloseButton']) {
      const button = this.scope.locator(`[data-dyn-controlname="${name}"]`).filter({ visible: true }).first();
      if (await button.count()) {
        await button.click();
        await this.waitForIdle();
        return;
      }
    }

    // Every D365 form and dialog closes on Escape, which is what the recorded
    // action usually was in the first place.
    await this.page.keyboard.press('Escape');
    await this.waitForIdle();
  }

  // -- interaction ----------------------------------------------------------

  async click(controlName: string, controlType = ''): Promise<void> {
    const control = await this.locate(controlName, controlType);
    await control.click();
    await this.waitForIdle();
  }

  /** `CommandName=TabShown` - an action-pane tab, section page or pivot. */
  async tab(controlName: string): Promise<void> {
    const tab = await this.locate(controlName, 'AppBarTab');
    await tab.click();
    await this.waitForIdle();
  }

  async setField(controlName: string, value: string, controlType = ''): Promise<void> {
    const control = await this.locate(controlName, controlType);

    if (isCheckbox(controlType)) {
      await this.setCheckbox(control, value);
      await this.waitForIdle();
      return;
    }

    // The recorded control name can land on a wrapper; the editable node is
    // then a descendant input.
    const target = await editable(control);

    const type = await target.getAttribute('type');
    if (type === 'checkbox') {
      await this.setCheckbox(target, value);
    } else {
      await target.click();
      await target.fill('');
      await target.fill(value);
      // Commit the edit; D365 validates on blur, not on keystroke.
      await target.press('Tab');
    }

    await this.waitForIdle();
  }

  private async setCheckbox(control: Locator, value: string): Promise<void> {
    const want = /^(true|yes|1|checked)$/i.test(value.trim());
    const box = control.locator('input[type="checkbox"]').first();
    const target = (await box.count()) ? box : control;

    const checked = await target.isChecked().catch(async () => {
      // A styled checkbox is a span, not an input, and reports its state
      // through ARIA instead.
      const aria = await target.getAttribute('aria-checked');
      return aria === 'true';
    });

    if (checked !== want) await target.click();
  }

  // -- lookups --------------------------------------------------------------

  /**
   * `CommandName=RequestPopup` - open the lookup on an input. What the user
   * then picked arrives as its own recorded action inside the lookup form, so
   * this only has to get the flyout open.
   */
  async openLookup(controlName: string): Promise<void> {
    const control = await this.locate(controlName, 'Input');
    await control.click();
    await this.waitForIdle();

    for (const selector of LOOKUP_SELECTORS) {
      const flyout = this.page.locator(selector).filter({ visible: true }).first();
      if (await flyout.count()) return;
    }

    // Some lookups only open on an explicit gesture rather than on focus.
    await control.press('Alt+ArrowDown').catch(() => undefined);
    await this.waitForIdle();
  }

  /** `CommandName=ResolveChanges` - commit what the lookup selected. */
  async commitLookup(controlName: string): Promise<void> {
    const control = await this.locate(controlName, 'Input');
    await editable(control).then((target) => target.press('Tab'));
    await this.waitForIdle();
  }

  /**
   * `CommandName=SelectionPathChanged` - pick a node in a tree.
   *
   * The recorder stores the path the way the tree renders it, backslash
   * separated and each segment usually of the form `Label (Code)`:
   *
   *     ALL (ALL)\Adventure Works (Adventure Works)
   *
   * Each segment is opened in turn, so a collapsed branch on the way down does
   * not stop the one below it from being reachable.
   */
  async selectTreeItem(controlName: string, path: string): Promise<void> {
    await this.walkTree(controlName, path, { selectLast: true });
  }

  private async walkTree(
    controlName: string,
    path: string,
    { selectLast }: { selectLast: boolean },
  ): Promise<void> {
    const tree = await this.locate(controlName, 'Tree');
    const segments = path.split('\\').filter((segment) => segment.trim().length > 0);

    if (!segments.length) {
      throw new Error(`Tree '${controlName}' was given an empty path.`);
    }

    for (const [index, segment] of segments.entries()) {
      const node = await this.treeNode(tree, segment, controlName, path);
      const last = index === segments.length - 1 && selectLast;

      if (last) {
        await node.click();
      } else {
        // Expand rather than select: selecting a branch on the way down can
        // reload the tree and lose the rest of the path.
        if ((await node.getAttribute('aria-expanded')) === 'false') {
          await node.click();
        } else {
          const expander = node.locator('.treeExpand, [aria-expanded="false"]').first();
          if (await expander.count()) await expander.click();
        }
      }

      await this.waitForIdle();
    }
  }

  private async treeNode(
    tree: Locator,
    segment: string,
    controlName: string,
    path: string,
  ): Promise<Locator> {
    // `Label (Code)` is how the tree renders a node, but the DOM sometimes
    // carries only the label, so both spellings are tried.
    const label = segment.replace(/\s*\(.*\)\s*$/, '').trim();

    for (const text of [segment, label]) {
      const node = await this.innermost(tree, TREE_NODE_SELECTOR, text);
      if (node) return node;
    }

    throw new Error(
      `Tree '${controlName}' has no node '${segment}' (from path '${path}').\n\n` +
        'Tree paths are replayed by the text the recorder captured, so a ' +
        'translated or renamed node will not be found.',
    );
  }

  /**
   * The innermost element matching `text`.
   *
   * A containment match is true of every ancestor as well, so in a tree the
   * first match for a leaf is the root that contains it - clicking which
   * re-collapses the branch instead of selecting anything. The deepest match is
   * the one that has no matching descendant of its own.
   */
  private async innermost(
    root: Locator,
    selector: string,
    text: string,
  ): Promise<Locator | undefined> {
    const candidates = root.locator(selector).filter({ hasText: text }).filter({ visible: true });

    for (let i = (await candidates.count()) - 1; i >= 0; i--) {
      const candidate = candidates.nth(i);
      const nested = candidate.locator(selector).filter({ hasText: text }).filter({ visible: true });
      if (!(await nested.count())) return candidate;
    }

    return undefined;
  }

  /**
   * `CommandName=ExpandingPath` - open a branch of a tree without selecting
   * it. Every segment on the way down is opened, the last one included.
   */
  async expandTreeItem(controlName: string, path: string): Promise<void> {
    await this.walkTree(controlName, path, { selectLast: false });
  }

  /** `CommandName=ResetFilters` - clear the filter pane. */
  async resetFilters(controlName: string): Promise<void> {
    const reset = this.page
      .locator('button, [role="button"]')
      .filter({ hasText: /^\s*Reset\s*$/ })
      .filter({ visible: true })
      .first();

    if (await reset.count()) {
      await reset.click();
      await this.waitForIdle();
      return;
    }

    // No pane open: the filter manager itself carries the reset affordance.
    const manager = await this.locate(controlName, 'FilterManager');
    await manager.click();
    await this.waitForIdle();
  }

  /**
   * `CommandName=ExecuteShortcuts` - a named client shortcut, such as the one
   * that flips a page between View and Edit mode.
   */
  async shortcut(name: string): Promise<void> {
    const controls = SHORTCUT_CONTROLS[name.toLowerCase()];

    if (!controls) {
      throw new Error(
        `Unknown client shortcut '${name}'.\n\n` +
          'Add it to SHORTCUT_CONTROLS in runtime/d365.ts, mapping it to the ' +
          'reserved control that does the same thing.',
      );
    }

    for (const control of controls) {
      const button = this.scope
        .locator(`[data-dyn-controlname="${control}"], [name="${control}"]`)
        .filter({ visible: true })
        .first();

      if (await button.count()) {
        await button.click();
        await this.waitForIdle();
        return;
      }
    }

    throw new Error(
      `Shortcut '${name}' maps to ${controls.join(' or ')}, none of which is on the page.`,
    );
  }

  // -- grids ----------------------------------------------------------------

  /**
   * Grids are virtualized: only rendered rows exist in the DOM, so a recorded
   * row index cannot be used as a raw nth() into the page. We scroll the grid
   * until the requested row materializes.
   */
  private async gridRow(gridName: string, row: number): Promise<Locator> {
    const grid = await this.locate(gridName, 'Grid');

    for (let attempt = 0; attempt < 20; attempt++) {
      for (const selector of GRID_ROW_SELECTORS(row)) {
        const candidate = grid.locator(selector).filter({ visible: true }).first();
        if (await candidate.count()) return candidate;
      }

      await grid.press('PageDown').catch(() => undefined);
      await this.waitForIdle();
    }

    throw new Error(
      `Grid '${gridName}' never rendered row ${row}.\n\n` +
        'Tried:\n' +
        GRID_ROW_SELECTORS(row)
          .map((s) => `  ${s}`)
          .join('\n') +
        '\n\nRow indexes are recorded zero-based and matched against the 1-based ' +
        '`aria-rowindex` the client renders. If your platform version numbers them ' +
        'differently, adjust GRID_ROW_SELECTORS in runtime/d365.ts.',
    );
  }

  /** The row the cursor is on: whatever `selectRow` last chose, else the client's. */
  private async activeRow(gridName: string): Promise<Locator> {
    const remembered = this.cursor.get(gridName);
    if (remembered !== undefined) return this.gridRow(gridName, remembered);

    const grid = await this.locate(gridName, 'Grid');
    for (const selector of ACTIVE_ROW_SELECTORS) {
      const row = grid.locator(selector).filter({ visible: true }).first();
      if (await row.count()) return row;
    }

    return this.gridRow(gridName, 0);
  }

  /**
   * A cell of `row` that is safe to click in order to select it.
   *
   * Clicking the row itself lands on its centre point, and if that point falls
   * on the row's hyperlink the client drills into the record instead of
   * selecting it - the grid is then gone, and every later step fails looking
   * for it. Which cell the centre lands on depends on font metrics, so this
   * reproduces on one platform and not another.
   */
  private async selectableCell(row: Locator): Promise<Locator> {
    const plain = row.locator(':scope > *').filter({ hasNot: this.page.locator('a') });
    return (await plain.count()) ? plain.last() : row;
  }

  /** `CommandName=ChangeSelectedIndexInCache` - move the grid cursor. */
  async selectRow(gridName: string, row: number): Promise<void> {
    const target = await this.gridRow(gridName, row);
    await (await this.selectableCell(target)).click();
    this.cursor.set(gridName, row);
    await this.waitForIdle();
  }

  /** `CommandName=MarkActiveRow` - tick the current row's selection box. */
  async markRow(gridName: string): Promise<void> {
    const row = await this.activeRow(gridName);
    const box = row.locator('input[type="checkbox"], .dyn-checkbox-span, [role="checkbox"]').first();

    if (await box.count()) {
      await box.click();
    } else {
      // No selection column: the client marks the row on a plain click - but
      // not on the link cell, which would open the record instead.
      await (await this.selectableCell(row)).click();
    }

    await this.waitForIdle();
  }

  /** `CommandName=NavigationAction` - follow the link in the current row. */
  async openRow(gridName: string): Promise<void> {
    const row = await this.activeRow(gridName);
    const link = row.locator('a, [role="link"], .dyn-hyperlink').first();

    if (await link.count()) {
      await link.click();
    } else {
      await row.dblclick();
    }

    // The cursor belongs to the grid we just left.
    this.cursor.delete(gridName);
    await this.waitForIdle();
  }

  async setGridCell(
    gridName: string,
    columnName: string,
    rowIndex: number,
    value: string,
    controlType = '',
  ): Promise<void> {
    const row = await this.gridRow(gridName, rowIndex);
    const cell = row.locator(`[data-dyn-controlname="${columnName}"], [name="${columnName}"]`).first();

    if (!(await cell.count())) {
      throw new Error(
        `Grid '${gridName}' row ${rowIndex} has no cell for column '${columnName}'.`,
      );
    }

    await cell.click();

    if (isCheckbox(controlType)) {
      await this.setCheckbox(cell, value);
    } else {
      const input = await editable(cell);
      await input.fill('');
      await input.fill(value);
      await input.press('Tab');
    }

    this.cursor.set(gridName, rowIndex);
    await this.waitForIdle();
  }

  // -- filtering ------------------------------------------------------------

  /**
   * `CommandName=ApplyFiltersForTaskRecorder` - reapply a column filter.
   *
   * The recorder stores this as JSON on the command rather than as a series of
   * clicks, so there is no click sequence to replay and this has to drive the
   * filter flyout itself. `label` is the column header the user actually
   * clicked; `field` is the underlying data field, used only as a fallback.
   *
   * Least verified helper in this file - check it first against a sandbox.
   */
  async filter(
    controlName: string,
    field: string,
    label: string,
    operator: string,
    value: string,
  ): Promise<void> {
    const header = await this.filterHeader(label, field);
    await header.click();
    await this.waitForIdle();

    // `.sysPopup` is the client's generic popup class - an overflow menu is one
    // too - so the flyout is identified by what it contains rather than by its
    // class alone.
    const flyout = this.page
      .locator('.filterFlyout, .sysPopup, [role="dialog"]')
      .filter({ visible: true })
      .filter({ has: this.page.locator('input:not([type="checkbox"]), textarea') })
      .last();

    if (operator) {
      const chooser = flyout.locator('select, [role="combobox"]').first();
      if (await chooser.count()) {
        await chooser.selectOption({ label: operatorLabel(operator) }).catch(() => undefined);
      }
    }

    const input = flyout.locator('input:not([type="checkbox"]), textarea').first();
    await input.fill(value);
    await input.press('Enter');
    await this.waitForIdle();
  }

  private async filterHeader(label: string, field: string): Promise<Locator> {
    if (label) {
      const byLabel = this.scope
        .locator(`[role="columnheader"]`)
        .filter({ hasText: label })
        .filter({ visible: true })
        .first();
      if (await byLabel.count()) return byLabel;
    }

    // Fall back to the field name, which is what the column control is
    // usually named after.
    return this.locate(field, 'Grid');
  }

  // -- assertions -----------------------------------------------------------

  async expectValue(controlName: string, expected: string, controlType = ''): Promise<void> {
    const control = await this.locate(controlName, controlType);
    const input = control.locator('input, textarea').first();

    if (await input.count()) {
      await expect(input).toHaveValue(expected);
    } else {
      await expect(control).toHaveText(expected);
    }
  }

  async expectVisible(controlName: string, controlType = ''): Promise<void> {
    await expect(await this.locate(controlName, controlType)).toBeVisible();
  }
}

function isCheckbox(controlType: string): boolean {
  return controlType.toLowerCase() === 'checkbox';
}

/** The editable node for a control: a descendant input, or the control itself. */
async function editable(control: Locator): Promise<Locator> {
  const input = control.locator('input, textarea').first();
  return (await input.count()) ? input : control;
}

/**
 * The recorder stores filter operators as their enum names; the flyout shows
 * them in English. Anything not listed is passed through unchanged.
 */
function operatorLabel(operator: string): string {
  const labels: Record<string, string> = {
    is: 'is exactly',
    isexactly: 'is exactly',
    matches: 'begins with',
    beginswith: 'begins with',
    contains: 'contains',
    doesnotcontain: 'does not contain',
    isoneof: 'is one of',
    greaterthan: 'after',
    lessthan: 'before',
    between: 'between',
  };

  return labels[operator.toLowerCase().replace(/[^a-z]/g, '')] ?? operator;
}
