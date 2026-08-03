// Minimal interop for the one thing C# cannot do on its own: talk to the
// browser's localStorage API. No app logic lives here — see Services/*.cs.
window.mealAppStorage = {
  get: (key) => window.localStorage.getItem(key),
  set: (key, value) => window.localStorage.setItem(key, value),
  remove: (key) => window.localStorage.removeItem(key),
};
