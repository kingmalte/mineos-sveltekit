import { describe, expect, it } from 'vitest';
import { createSseParser, type ParsedEvent } from './sseParser';

function parse(...chunks: string[]): ParsedEvent[] {
	const events: ParsedEvent[] = [];
	const parser = createSseParser((e) => events.push(e));
	for (const chunk of chunks) parser.push(chunk);
	return events;
}

describe('createSseParser', () => {
	it('dispatches a data line on the blank line that ends it', () => {
		expect(parse('data: {"a":1}\n\n')).toEqual([{ event: 'message', data: '{"a":1}' }]);
	});

	it('does not dispatch until the event is complete', () => {
		expect(parse('data: {"a":1}\n')).toEqual([]);
	});

	it('joins multi-line data with newlines', () => {
		expect(parse('data: one\ndata: two\n\n')).toEqual([{ event: 'message', data: 'one\ntwo' }]);
	});

	it('drops comment lines, such as the keep-alive heartbeat', () => {
		expect(parse(': keep-alive\n\n', 'data: x\n\n')).toEqual([{ event: 'message', data: 'x' }]);
	});

	it('reassembles events split across chunks, including mid-line', () => {
		expect(parse('da', 'ta: hel', 'lo\n', '\n')).toEqual([{ event: 'message', data: 'hello' }]);
	});

	it('handles CRLF line endings, even when split between chunks', () => {
		expect(parse('data: a\r', '\n\r\n')).toEqual([{ event: 'message', data: 'a' }]);
		expect(parse('data: b\r\n\r\n')).toEqual([{ event: 'message', data: 'b' }]);
	});

	it('keeps event names and ids', () => {
		expect(parse('event: status\nid: 4\ndata: up\n\n')).toEqual([
			{ event: 'status', data: 'up', id: '4' }
		]);
	});

	it('only strips a single leading space from the value', () => {
		expect(parse('data:  indented\n\ndata:tight\n\n')).toEqual([
			{ event: 'message', data: ' indented' },
			{ event: 'message', data: 'tight' }
		]);
	});
});
