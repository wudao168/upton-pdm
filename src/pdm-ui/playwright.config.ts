import { defineConfig } from '@playwright/test'

const host = process.env.PDM_E2E_HOST || '127.0.0.1'
const port = process.env.PDM_E2E_PORT || '5193'
const origin = `http://${host}:${port}`

export default defineConfig({
  testDir: './tests/e2e',
  timeout: 30_000,
  use: {
    baseURL: origin,
    channel: 'chrome',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  webServer: {
    command: process.env.PDM_E2E_BUILD_DIR
      ? `node node_modules/vite/bin/vite.js preview --outDir "${process.env.PDM_E2E_BUILD_DIR}" --host ${host} --port ${port} --strictPort`
      : `node node_modules/vite/bin/vite.js --host ${host} --port ${port} --strictPort`,
    url: origin,
    reuseExistingServer: false,
    timeout: 60_000,
  },
})
