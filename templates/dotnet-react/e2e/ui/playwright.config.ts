import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  timeout: 60_000,
  reporter: 'list',
  use: { baseURL: 'http://127.0.0.1:5173' },
  // The API is started by the factory (`verify.start`). Playwright starts the Vite dev server, which forwards /api to the API.
  webServer: {
    command: 'sh -c "npm ci --no-audit --no-fund && npm run dev"',
    cwd: '../../frontend',
    url: 'http://127.0.0.1:5173',
    reuseExistingServer: true,
    timeout: 180_000,
  },
});
