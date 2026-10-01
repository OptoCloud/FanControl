// The stored theme choice, applied before first paint (see app.html). It reads one key of
// localStorage and writes one data attribute; nothing from outside reaches it.
try {
	const stored = localStorage.getItem('theme');
	if (stored === 'light' || stored === 'dark') document.documentElement.dataset.theme = stored;
} catch {
	// Private mode, or site data blocked: the OS preference decides instead.
}
