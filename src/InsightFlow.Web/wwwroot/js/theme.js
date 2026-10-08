// Dark-mode toggle. Applied before first paint (classic script in <head>) so there is no flash of the wrong theme.
(function () {
  const KEY = "insightflow.theme";
  const stored = (() => { try { return localStorage.getItem(KEY); } catch { return null; } })();
  const dark = stored ? stored === "dark" : window.matchMedia("(prefers-color-scheme: dark)").matches;
  document.documentElement.classList.toggle("dark", dark);

  window.insightFlowTheme = {
    isDark: () => document.documentElement.classList.contains("dark"),
    toggle: () => {
      const next = !document.documentElement.classList.contains("dark");
      document.documentElement.classList.toggle("dark", next);
      try { localStorage.setItem(KEY, next ? "dark" : "light"); } catch { /* storage unavailable */ }
      return next;
    },
  };
})();
