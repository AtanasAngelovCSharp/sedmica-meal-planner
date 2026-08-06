// Minimal interop for the things C# cannot do on its own: talk to the
// browser's localStorage API, and read OS-level media-query preferences.
// No app logic lives here — see Services/*.cs.
window.mealAppStorage = {
  get: (key) => window.localStorage.getItem(key),
  set: (key, value) => window.localStorage.setItem(key, value),
  remove: (key) => window.localStorage.removeItem(key),
};

window.mealAppInterop = {
  prefersReducedMotion: () => window.matchMedia("(prefers-reduced-motion: reduce)").matches,
};
