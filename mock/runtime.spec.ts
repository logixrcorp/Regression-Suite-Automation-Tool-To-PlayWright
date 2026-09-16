/**
 * The runtime helper's own tests, against the mock.
 *
 * Hand-written, unlike everything under `tests/`, and deliberately kept out of
 * that folder so the real-environment config never picks it up.
 *
 * Each case here covers a branch of runtime/d365.ts that the generated spec
 * does not reach: a stale form the client left in the DOM, a grid that renders
 * three rows at a time, two dialogs sharing a button name, and a value the
 * client rewrites after you type it.
 *
 * Note on what this proves: the two visibility filters - the one in `locate()`
 * and the one in `withForm()` - are redundant for the stale-form case. Removing
 * either alone still passes; removing both fails here. So this guards the pair,
 * not each individually.
 */
import { expect, test } from '@playwright/test';
import { D365 } from '../runtime/d365';

test('stale form, virtualized grid, stacked dialog, rewritten value', async ({ page }) => {
  const d365 = await D365.open(page);

  // 1. The stale form is first in the DOM and carries the same control names.
  await expect(page.locator('#staleForm input[name="PurchTable_DeliveryDate"]')).toHaveValue('STALE');

  await d365.withForm('PurchTable', async () => {
    // 2. Row 5 is outside the rendered window: gridRow() has to scroll for it.
    await expect(page.locator('[aria-rowindex="6"]')).toHaveCount(0);
    await d365.selectRow('Grid', 5);
    await expect(page.locator('#gridWindow')).toHaveText('3');
    await d365.openRow('Grid');
  });

  await expect(page.locator('#openedRow')).toHaveText('000126');

  await d365.withForm('PurchTable', async () => {
    // 3. The live field, not the stale one that shares its name.
    await d365.setField('PurchTable_DeliveryDate', '9/30/2026', 'Date');
    await expect(page.locator('#detailSection input[name="PurchTable_DeliveryDate"]')).toHaveValue('9/30/2026');
    await expect(page.locator('#staleForm input[name="PurchTable_DeliveryDate"]')).toHaveValue('STALE');

    // 4. A value the client rewrites on blur: ours went in, theirs came back.
    await d365.setGridCell('PurchLineGrid', 'PurchLine_PurchQty', 0, '12', 'Real');
    await expect(page.locator('input[name="PurchLine_PurchQty"]')).toHaveValue('12.00');

    // 5. Two dialogs, both with a button called OkButton. Scoping must pick
    //    the one on top; the one underneath sets "WRONG DIALOG".
    await d365.click('PurchTable_PostPackingSlip', 'CommandButton');
    await d365.withForm('PurchFormLetterParmData', async () => {
      await d365.click('OkButton', 'CommandButton');
    });
  });

  await expect(page.locator('#activeTab')).toHaveText('Posted');
});
