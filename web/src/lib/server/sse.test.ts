import { describe, expect, it } from 'vitest';
import { SseParser } from './sse';

describe('SseParser', () => {
	it('parses a complete event', () => {
		expect(new SseParser().push('event: status\ndata: {"a":1}\n\n')).toEqual([{ event: 'status', data: '{"a":1}' }]);
	});

	it('reassembles an event split across arbitrary chunk boundaries', () => {
		const parser = new SseParser();
		const stream = 'event: status\ndata: {"timestampUtc":"t"}\n\n';
		const events = [...stream].flatMap((character) => parser.push(character));

		expect(events).toEqual([{ event: 'status', data: '{"timestampUtc":"t"}' }]);
	});

	it('returns several events from one chunk and keeps the unfinished tail', () => {
		const parser = new SseParser();

		expect(parser.push('data: 1\n\ndata: 2\n\ndata: 3')).toEqual([
			{ event: 'message', data: '1' },
			{ event: 'message', data: '2' }
		]);
		expect(parser.push('\n\n')).toEqual([{ event: 'message', data: '3' }]);
	});

	it('ignores keepalive comments and does not leak an event name into the next event', () => {
		const parser = new SseParser();

		expect(parser.push(': keepalive\n\n')).toEqual([]);
		expect(parser.push('event: status\ndata: a\n\ndata: b\n\n')).toEqual([
			{ event: 'status', data: 'a' },
			{ event: 'message', data: 'b' }
		]);
	});

	it('handles CRLF line endings and multi-line data', () => {
		expect(new SseParser().push('data: one\r\ndata: two\r\n\r\n')).toEqual([{ event: 'message', data: 'one\ntwo' }]);
	});
});
