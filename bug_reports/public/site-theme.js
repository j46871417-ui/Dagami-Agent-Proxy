// Apply the theme before styles are painted.
(() => {
  let mode = 'system';
  try { mode = localStorage.getItem('dagami_theme') || mode; } catch {}
  if (!['system', 'light', 'dark'].includes(mode)) mode = 'system';
  const dark = mode === 'dark' || (mode === 'system' && matchMedia('(prefers-color-scheme: dark)').matches);
  document.documentElement.dataset.theme = dark ? 'dark' : 'light';
  document.documentElement.dataset.themeMode = mode;
})();
