import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

// Storefront.Service has no CORS middleware - by design, it's meant to be
// the single browser-facing origin (it serves this app's own production
// build from wwwroot). In dev, Vite's own proxy stands in for that same-
// origin relationship instead of adding CORS to the backend just for this.
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const bffUrl = env.VITE_BFF_URL || 'http://localhost:8089';

  return {
    plugins: [react()],
    server: {
      port: 5173,
      proxy: {
        '/api': {
          target: bffUrl,
          changeOrigin: true,
        },
      },
    },
  };
});
