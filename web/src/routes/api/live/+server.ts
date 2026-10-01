import { error } from '@sveltejs/kit';
import { getCore } from '$lib/server/core';
import type { LiveMessage } from '$lib/types';
import { STREAM_KEEPALIVE_MS } from '$lib/limits';
import type { RequestHandler } from './$types';

// Server-Sent Events to the browser: vigil-core's live stream as vigil-web mirrors it, plus
// whether vigil-web can reach vigil-core at all.

export const GET: RequestHandler = () => {
	const core = getCore();
	const encoder = new TextEncoder();
	let unsubscribe: (() => void) | null = null;
	let keepalive: NodeJS.Timeout;

	const stream = new ReadableStream<Uint8Array>({
		start(controller) {
			const send = (message: LiveMessage) => controller.enqueue(encoder.encode(`data: ${JSON.stringify(message)}\n\n`));

			// Taken before anything is sent, so a refused stream costs nothing (SECURITY.md §4.2).
			unsubscribe = core.subscribe(send);
			if (!unsubscribe) error(503, 'Too many live streams are open. Close a tab and reload.');

			// Current state first, so a new tab doesn't sit empty until the next poll.
			for (const message of core.currentState) send(message);

			keepalive = setInterval(() => controller.enqueue(encoder.encode(': keepalive\n\n')), STREAM_KEEPALIVE_MS);
		},
		cancel() {
			unsubscribe?.();
			clearInterval(keepalive);
		}
	});

	return new Response(stream, {
		headers: {
			'Content-Type': 'text/event-stream',
			'Cache-Control': 'no-cache, no-transform',
			// Stops nginx-style proxies from buffering the stream into uselessness.
			'X-Accel-Buffering': 'no'
		}
	});
};
