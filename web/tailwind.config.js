export default {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    extend: {
      colors: {
        ok: "#22c55e", warn: "#eab308", crit: "#ef4444",
        panel: "#161b22", panelborder: "#30363d", bg: "#0d1117"
      }
    }
  },
  plugins: []
};
