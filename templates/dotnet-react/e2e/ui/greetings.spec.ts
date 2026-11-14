import { mkdirSync, writeFileSync } from 'node:fs';
import { expect, test } from '@playwright/test';

// The page as plain text, for the UX reviewer (it cannot see screenshots). The folder is removed before anything is pushed.
async function writeEvidence(page: import('@playwright/test').Page, name: string) {
  mkdirSync('../evidence', { recursive: true });
  writeFileSync(`../evidence/${name}.txt`, await page.locator('body').ariaSnapshot());
}

test('the page greets a valid name through the API', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Greetings' })).toBeVisible();
  await writeEvidence(page, 'start');

  await page.getByLabel('Your name').fill('Ada');
  await page.getByRole('button', { name: 'Greet' }).click();
  await expect(page.getByRole('status')).toHaveText('Hello, Ada!');
  await writeEvidence(page, 'greeted');
});

test('the page shows the API\'s reason for refusing a name', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Your name').fill('<script>');
  await page.getByRole('button', { name: 'Greet' }).click();
  await expect(page.getByRole('status')).toContainText('letters, digits, spaces, dots or dashes');
  await writeEvidence(page, 'refused');
});
