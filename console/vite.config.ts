/// <reference types="vitest" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// `npm run dev` proxies the API to an operator running with CONSOLE_ENABLED (docker compose: 9100).
export default defineConfig({
  plugins: [react()],
  server: {
    port: 3100,
    proxy: {
      '/api': { target: process.env.CONSOLE_API_URL ?? 'http://localhost:9100', changeOrigin: true },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: { modules: { classNameStrategy: 'non-scoped' } },
  },
})
