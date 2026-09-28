import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'

const projectRoot = fileURLToPath(new URL('.', import.meta.url))
const require = createRequire(import.meta.url)
const vueRequire = createRequire(require.resolve('vue/package.json'))
const vueShared = vueRequire.resolve('@vue/shared/dist/shared.esm-bundler.js')

export default defineConfig({
  base: './',
  plugins: [vue()],
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
      },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./tests/setup.ts'],
    include: ['./tests/**/*.test.ts'],
  },
})
