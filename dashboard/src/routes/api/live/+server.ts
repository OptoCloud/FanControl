import { getRuntime } from '$lib/server/runtime';
import type { LiveMessage } from '$lib/types';
import type { RequestHandler } from './$types';

// Server-Sent Events to the browser: the daemon's snapshots as they arrive, plus this
// server's own additions (daemon reachability, new events, last-good drive health).
export const GET: RequestHandler = () => {
	const runtime = getRuntime();
	const encoder = new TextEncoder();
	let unsubscribe = () => {};
	let keepalive: NodeJS.Timeout;

	const stream = new ReadableStream<Uint8Array>({
		start(controller) {
			const send = (message: LiveMessage) => controller.enqueue(encoder.encode(`data: ${JSON.stringify(message)}\n\n`));

			// Current state first, so a new tab doesn't sit empty until the next poll.
			send({ type: 'daemon', connected: runtime.daemonConnected });
			if (runtime.latest) send({ type: 'snapshot', snapshot: runtime.latest });
			send({ type: 'drives', drives: runtime.driveList });

			unsubscribe = runtime.subscribe(send);
			keepalive = setInterval(() => controller.enqueue(encoder.encode(': keepalive\n\n')), 20_000);
		},
		cancel() {
			unsubscribe();
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
