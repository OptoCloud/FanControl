import adapter from '@sveltejs/adapter-node';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vitest/config';

export default defineConfig({
	plugins: [
		sveltekit({
			compilerOptions: {
				// Force runes mode for the project, except for libraries. Can be removed in svelte 6.
				runes: ({ filename }) => (filename.split(/[/\\]/).includes('node_modules') ? undefined : true)
			},

			// A long-lived Node server: it holds one connection to the daemon, writes history
			// to Postgres and fans live data out to browsers. None of that fits serverless.
			adapter: adapter(),

			// docs/SECURITY.md §8. vigil-web ships everything it serves, so no external origin
			// is allowed anywhere. 'nonce' rather than 'hash': every page here is rendered per
			// request (nothing is prerendered), and it is what lets app.html's theme script run
			// without opening the door to any other inline script.
			csp: {
				mode: 'nonce',
				directives: {
					'default-src': ['self'],
					'script-src': ['self'],
					// 'unsafe-inline' is required by Svelte's `style:` directive, which renders as
					// a style ATTRIBUTE (the fan and UPS meters use it for their width). It does
					// not permit a <script>; script-src above governs those.
					'style-src': ['self', 'unsafe-inline'],
					'img-src': ['self', 'data:'],
					'font-src': ['self'],
					// The live stream (EventSource) and the history endpoint, both same-origin.
					'connect-src': ['self'],
					'object-src': ['none'],
					'base-uri': ['self'],
					// vigil-web has no form and must never be framed.
					'form-action': ['none'],
					'frame-ancestors': ['none']
				}
			}
		})
	],
	test: {
		include: ['src/**/*.test.ts']
	}
});
