import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  // In development the page asks Vite for /api/..., and Vite forwards it to the .NET API, so the browser sees one origin.
  server: { port: 5173, strictPort: true, proxy: { '/api': 'http://localhost:5080' } },
  test: { environment: 'jsdom', setupFiles: ['./src/test-setup.ts'] },
});
