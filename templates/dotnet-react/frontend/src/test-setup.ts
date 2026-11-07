import '@testing-library/jest-dom/vitest';
import { cleanup, configure } from '@testing-library/react';
import { afterEach, vi } from 'vitest';

// findBy* and waitFor give up after 1 s by default, measured on the wall clock; see the note in vite.config.ts.
configure({ asyncUtilTimeout: 10_000 });

afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
