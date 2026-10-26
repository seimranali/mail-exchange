/// <reference types="vitest/config" />
import { fileURLToPath, URL } from 'node:url'
import react from '@vitejs/plugin-react'
import { defineConfig, type Plugin } from 'vite'

// Stamped into asset file names so every release uses URLs no browser or CDN has cached before.
const buildId = (process.env.MX_BUILD_ID ?? Date.now().toString(36)).replace(/[^a-zA-Z0-9.-]/g, '')

/** Versions the non-hashed theme bootstrap script reference in index.html. */
function versionThemeScript(): Plugin {
  return {
    name: 'mx-version-theme-script',
    transformIndexHtml: (html) => html.replace('src="/theme-init.js"', `src="/theme-init.js?v=${buildId}"`),
  }
}

export default defineConfig({
  plugins: [react(), versionThemeScript()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  css: {
    modules: { localsConvention: 'camelCaseOnly' },
  },
  build: {
    rollupOptions: {
      output: {
        entryFileNames: `assets/[name]-[hash]-${buildId}.js`,
        chunkFileNames: `assets/[name]-[hash]-${buildId}.js`,
        assetFileNames: `assets/[name]-[hash]-${buildId}[extname]`,
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5134',
      '/hubs': { target: 'http://localhost:5134', ws: true },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: { modules: { classNameStrategy: 'non-scoped' } },
  },
})
