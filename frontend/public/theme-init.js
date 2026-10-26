// Applies the saved theme before first paint (no flash of the wrong theme).
// A separate file rather than an inline script, so the Content-Security-Policy can forbid inline code.
try {
  var t = localStorage.getItem('mx-theme')
  if (t === 'light' || t === 'dark') document.documentElement.dataset.theme = t
} catch (e) {}
