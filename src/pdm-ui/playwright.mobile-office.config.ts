import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './tests/e2e', testMatch: /mobile-office(?:-layout)?\.spec\.ts/, timeout: 45_000,
  outputDir: process.env.TEMP + '/uplm-mobile-office-playwright',
  use: { baseURL: process.env.UPLM_MOBILE_TEST_URL || 'http://192.168.2.8:5194', channel: 'chrome', screenshot: 'only-on-failure' },
  webServer: { command: 'node node_modules/vite/bin/vite.js --host 0.0.0.0 --port 5194 --strictPort', url: 'http://127.0.0.1:5194', reuseExistingServer: true, timeout: 60_000 },
})
