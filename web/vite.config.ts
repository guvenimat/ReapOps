import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  build: { outDir: '../src/ReapOps.Api/wwwroot', emptyOutDir: true },
  server: {
    port: 8091,
    proxy: { '/api': 'http://localhost:8090' },
  },
})
