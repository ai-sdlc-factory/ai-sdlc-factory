import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: { port: 5173, strictPort: true },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test-setup.ts'],
    // Generous on purpose: on a machine whose wall clock steps by several seconds (WSL2 can do this), the default 5 s limit fires early
    // in tests that take milliseconds. A real hang still fails, just after 30 s instead of 5 s.
    testTimeout: 30_000,
    hookTimeout: 30_000,
  },
});
