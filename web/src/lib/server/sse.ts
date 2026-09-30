// Incremental Server-Sent Events parser. Network chunks split anywhere (mid-line, even
// mid-character once decoded upstream), so this buffers and only acts on complete lines.

export interface SseEvent {
	event: string;
	data: string;
}

export class SseParser {
	private buffer = '';
	private event = '';
	private data: string[] = [];

	/** Feeds one decoded chunk; returns every event it completed. */
	push(chunk: string): SseEvent[] {
		this.buffer += chunk;
		const completed: SseEvent[] = [];

		let newline: number;
		while ((newline = this.buffer.indexOf('\n')) >= 0) {
			const line = this.buffer.slice(0, newline).replace(/\r$/, '');
			this.buffer = this.buffer.slice(newline + 1);

			if (line === '') {
				// A blank line dispatches whatever has accumulated.
				if (this.data.length > 0) {
					completed.push({ event: this.event || 'message', data: this.data.join('\n') });
				}
				this.event = '';
				this.data = [];
			} else if (line.startsWith(':')) {
				// Comment, which is how the daemon sends keepalives.
			} else {
				const colon = line.indexOf(':');
				const field = colon < 0 ? line : line.slice(0, colon);
				const value = colon < 0 ? '' : line.slice(colon + 1).replace(/^ /, '');
				if (field === 'event') this.event = value;
				else if (field === 'data') this.data.push(value);
			}
		}

		return completed;
	}
}
