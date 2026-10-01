import adapter from '@sveltejs/adapter-static';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vitest/config';

/**
 * Where `npm run dev` sends the API. The page is a static build that vigil-core serves next to
 * /api, so in production every request is same-origin; in development Vite serves the page and
 * this proxy makes the API look same-origin too. 3001 is vigil-core's default port
 * (Limits.CoreDefaultPort in core/).
 */
const CORE_URL = process.env.VIGIL_CORE_URL ?? 'http://127.0.0.1:3001';

export default defineConfig({
	plugins: [
		sveltekit({
			compilerOptions: {
				// Force runes mode for the project, except for libraries. Can be removed in svelte 6.
				runes: ({ filename }) => (filename.split(/[/\\]/).includes('node_modules') ? undefined : true)
			},

			// Files only: vigil-core serves build/ from WEB_ROOT, and everything live comes from
			// its API. There is no Node process in the deployment. One page, prerendered, so no
			// SPA fallback either: an unknown path is a 404 from vigil-core.
			adapter: adapter({ pages: 'build', assets: 'build', fallback: undefined, strict: true }),

			// docs/SECURITY.md §8. Everything served is shipped here, so no external origin is
			// allowed anywhere. 'hash' because the page is prerendered: there is no per-response
			// nonce to give, so SvelteKit writes a <meta> policy holding the hash of each inline
			// script it emits. The theme script is a file (static/theme.js) for the same reason,
			// so no hash of ours has to be kept in step by hand.
			//
			// frame-ancestors is not here: a <meta> policy cannot carry it and browsers ignore it
			// there. vigil-core sends it as a header on every response, with object-src, base-uri
			// and form-action again, which is where the framing protection actually takes effect.
			csp: {
				mode: 'hash',
				directives: {
					'default-src': ['self'],
					'script-src': ['self'],
					// 'unsafe-inline' is required by Svelte's `style:` directive, which renders as
					// a style ATTRIBUTE (the fan and UPS meters use it for their width). It does
					// not permit a <script>; script-src above governs those.
					'style-src': ['self', 'unsafe-inline'],
					'img-src': ['self', 'data:'],
					'font-src': ['self'],
					// The live stream (EventSource), history and the event log, all same-origin.
					'connect-src': ['self'],
					'object-src': ['none'],
					'base-uri': ['self'],
					// The dashboard has no form.
					'form-action': ['none']
				}
			}
		})
	],
	server: {
		proxy: {
			'/api': CORE_URL,
			'/health': CORE_URL
		}
	},
	test: {
		include: ['src/**/*.test.ts']
	}
});
