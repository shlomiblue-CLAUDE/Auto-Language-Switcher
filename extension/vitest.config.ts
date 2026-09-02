import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    environment: 'node',
    include: ['tests/**/*.test.ts'],
    // The WhatsApp adapter tests opt into jsdom per file via a docblock pragma, so the
    // corpus tests stay fast in plain node.
    environmentMatchGlobs: [['tests/**/*.dom.test.ts', 'jsdom']],
  },
});
