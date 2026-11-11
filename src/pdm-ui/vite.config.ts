import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import { readFileSync } from 'node:fs'

const projectRoot = fileURLToPath(new URL('.', import.meta.url))
const require = createRequire(import.meta.url)
const { generatePlmIconAssets } = require(projectRoot + 'tools/generate-plm-icon-assets.cjs')
const vueRequire = createRequire(require.resolve('vue/package.json'))
const vueShared = vueRequire.resolve('@vue/shared/dist/shared.esm-bundler.js')

export default defineConfig({
  base: './',
  plugins: [{
    name: 'plm-shared-icon-assets',
    async configResolved() { await generatePlmIconAssets() },
    async handleHotUpdate({ file }) {
      if (file.replaceAll('\\', '/').endsWith('/src/assets/plm-app-icon.svg') || file.replaceAll('\\', '/').endsWith('/src/assets/upton-full-brand.svg')) await generatePlmIconAssets()
    },
  }, {
    name: 'sandboxed-login-scenes',
    configureServer(server) {
      // 沙箱动画是独立文档，不能注入需要同源访问的 Vite 客户端。
      server.middlewares.use((request, response, next) => {
        const url = new URL(request.url ?? '/', 'http://localhost')
        if (!/^\/src\/assets\/(?:melon|pirate)-jelly\.html$/.test(url.pathname) || url.searchParams.has('import')) return next()
        response.setHeader('Content-Type', 'text/html; charset=utf-8')
        response.end(readFileSync(projectRoot + url.pathname.slice(1)))
      })
    },
  }, vue()],
  resolve: {
    // Element Plus imports this Vue runtime helper directly. Resolve it from
    // Vue's own dependency tree so pnpm's isolated linker can bundle it.
    alias: { '@vue/shared': vueShared },
  },
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': 'http://127.0.0.1:5080',
      '/health': 'http://127.0.0.1:5080',
    },
  },
  build: {
    target: 'es2018',
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: true,
    rollupOptions: {
      input: {
        main: `${projectRoot}index.html`,
        reviewOverlay: `${projectRoot}review-overlay.html`,
        serverSettings: `${projectRoot}server-settings.html`,
      },
    },
  },
  test: {
    environment: 'jsdom',
    // Node 26 的原生 Web Storage 会遮蔽 jsdom 存储，测试进程必须使用浏览器语义。
    execArgv: process.allowedNodeEnvironmentFlags.has('--no-experimental-webstorage') ? ['--no-experimental-webstorage'] : [],
    maxWorkers: 2,
    testTimeout: 15000,
    setupFiles: ['./tests/setup.ts'],
    include: ['./tests/**/*.test.ts'],
  },
})
