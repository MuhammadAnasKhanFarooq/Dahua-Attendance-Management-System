import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Allow Vite dev proxy to follow local development backend redirects (including self-signed certificates)
process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'https://localhost:7208',
        changeOrigin: true,
        secure: false,
      },
    },
  },
})
