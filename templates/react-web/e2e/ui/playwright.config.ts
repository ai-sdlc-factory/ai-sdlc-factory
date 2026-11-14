import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  timeout: 60_000,
  reporter: 'list',
  use: { baseURL: 'http://127.0.0.1:5173' },
  // The factory's `verify` starts nothing for this project: Playwright starts the dev server itself and waits for it.
  webServer: {
    command: 'sh -c "npm ci --no-audit --no-fund && npm run dev"',
    cwd: '../..',
    url: 'http://127.0.0.1:5173',
    reuseExistingServer: true,
    timeout: 180_000,
  },
});
