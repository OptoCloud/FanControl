import type { Handle, ServerInit } from '@sveltejs/kit';
import { building } from '$app/environment';
import { getCore } from '$lib/server/core';

// Connect to vigil-core as soon as the server boots, so the first page view already has state.
export const init: ServerInit = () => {
	if (!building) getCore();
};

/**
 * The headers that are not CSP. CSP itself is configured in vite.config.ts, where SvelteKit can
 * add the per-response nonce app.html's theme script needs. docs/SECURITY.md §8.
 *
 * No Strict-Transport-Security: this deployment serves plain HTTP on the LAN by decision
 * (ADR-008), where HSTS does nothing. It belongs with a TLS terminator, if one ever arrives.
 */
const SECURITY_HEADERS: Record<string, string> = {
	// vigil-web serves JSON, SSE and HTML; none of it should ever be content-sniffed.
	'X-Content-Type-Options': 'nosniff',
	// The dashboard links nowhere outward, so no referrer ever needs to leave.
	'Referrer-Policy': 'no-referrer',
	// Belt and braces with the frame-ancestors directive, for anything that predates CSP.
	'X-Frame-Options': 'DENY',
	'Cross-Origin-Opener-Policy': 'same-origin',
	// A read-only dashboard needs none of these, and a browser that is told so cannot be
	// talked into granting them.
	'Permissions-Policy': 'accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()'
};

export const handle: Handle = async ({ event, resolve }) => {
	const response = await resolve(event);
	for (const [header, value] of Object.entries(SECURITY_HEADERS)) {
		response.headers.set(header, value);
	}
	return response;
};
