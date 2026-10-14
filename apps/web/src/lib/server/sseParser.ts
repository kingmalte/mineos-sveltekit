/**
 * Incremental parser for a `text/event-stream` body, following the WHATWG
 * EventSource rules closely enough for the streams MineOS proxies: `data:`
 * lines accumulate, `event:` names the event, a blank line dispatches, and
 * comment lines (`: keep-alive`) are dropped.
 */
export type ParsedEvent = { event: string; data: string; id?: string };

export function createSseParser(onEvent: (event: ParsedEvent) => void) {
	let buffer = '';
	let data: string[] = [];
	let eventName = '';
	let lastId: string | undefined;

	function dispatch() {
		if (data.length > 0) {
			onEvent({ event: eventName || 'message', data: data.join('\n'), id: lastId });
		}
		data = [];
		eventName = '';
	}

	function line(text: string) {
		if (text === '') {
			dispatch();
			return;
		}
		if (text.startsWith(':')) return;

		const colon = text.indexOf(':');
		const field = colon === -1 ? text : text.slice(0, colon);
		let value = colon === -1 ? '' : text.slice(colon + 1);
		if (value.startsWith(' ')) value = value.slice(1);

		if (field === 'data') data.push(value);
		else if (field === 'event') eventName = value;
		else if (field === 'id' && !value.includes('\0')) lastId = value;
	}

	return {
		push(chunk: string) {
			buffer += chunk;
			// A trailing '\r' may be the first half of a '\r\n' split across
			// chunks, so leave it in the buffer until the next chunk arrives.
			let start = 0;
			for (let i = 0; i < buffer.length; i++) {
				const ch = buffer[i];
				if (ch !== '\n' && ch !== '\r') continue;
				if (ch === '\r' && i === buffer.length - 1) break;
				line(buffer.slice(start, i));
				if (ch === '\r' && buffer[i + 1] === '\n') i++;
				start = i + 1;
			}
			buffer = buffer.slice(start);
		}
	};
}
