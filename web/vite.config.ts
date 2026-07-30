import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Build-Output nach ../src/Fdash.Api/wwwroot -> ein Prozess, ein Port (Plan §7)
export default defineConfig({
  plugins: [react()],
  build: { outDir: "../src/Fdash.Api/wwwroot", emptyOutDir: true },
  server: {
    proxy: {
      "/api": "http://localhost:5000",
      "/hub": { target: "http://localhost:5000", ws: true }
    }
  }
});
