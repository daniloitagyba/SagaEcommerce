import '@testing-library/jest-dom/vitest';
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

// vitest.config.ts doesn't set test.globals, so RTL's own auto-cleanup
// (which only registers when it detects a global afterEach) never fires -
// every test file was leaking its rendered DOM into the next test, only
// invisible so far because no two tests in the same file rendered
// elements with colliding queries.
afterEach(cleanup);
