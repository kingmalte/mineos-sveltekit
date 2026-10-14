import { afterEach, describe, expect, it, vi } from 'vitest';
import {
	MAX_SUBSCRIPTIONS_PER_CONNECTION,
	closeConnection,
	connectionCount,
	isAllowedStreamPath,
	openConnection,
	subscribe,
	unsubscribe,
	type HubFrame
} from './sseHub';

const encoder = new TextEncoder();

/** An upstream SSE response the test writes to by hand. */
function upstream(init: { status?: number; type?: string } = {}) {
	let controller!: ReadableStreamDefaultController<Uint8Array>;
	let cancelled = false;
	const body = new ReadableStream<Uint8Array>({
		start(c) {
			controller = c;
		},
		cancel() {
			cancelled = true;
		}
	});
	const response = new Response(body, {
		status: init.status ?? 200,
		headers: { 'content-type': init.type ?? 'text/event-stream' }
	});
	return {
		response,
		send: (text: string) => controller.enqueue(encoder.encode(text)),
		end: () => controller.close(),
		get cancelled() {
			return cancelled;
		}
	};
}

const tick = () => new Promise((r) => setTimeout(r, 0));

function hub(owner = 'user-1') {
	const frames: HubFrame[] = [];
	const id = openConnection(owner, (f) => frames.push(f));
	return { id, frames, owner };
}

const opened: string[] = [];
afterEach(() => {
	for (const id of opened.splice(0)) closeConnection(id);
});

function track<T extends { id: string }>(h: T): T {
	opened.push(h.id);
	return h;
}

describe('isAllowedStreamPath', () => {
	it('allows same-origin /api paths with a query', () => {
		expect(isAllowedStreamPath('/api/servers/a/console/stream?source=java')).toBe(true);
	});

	it.each([
		['absolute URL', 'http://evil.example/api/x'],
		['protocol-relative URL', '//evil.example/api/x'],
		['non-api path', '/login'],
		['the hub itself', '/api/events'],
		['below the hub', '/api/events/x'],
		['dot segments that leave /api', '/api/../login'],
		['non-string', 42]
	])('rejects %s', (_label, path) => {
		expect(isAllowedStreamPath(path)).toBe(false);
	});
});

describe('sseHub', () => {
	it('greets a new connection with its id', () => {
		const h = track(hub());
		expect(h.frames).toEqual([{ t: 'hello', conn: h.id }]);
	});

	it('forwards upstream events tagged with the subscription id', async () => {
		const h = track(hub());
		const up = upstream();
		const fetcher = vi.fn(async () => up.response);

		expect(subscribe(h.id, h.owner, 's1', '/api/jobs/stream', fetcher)).toBe('ok');
		await tick();
		up.send('data: {"n":1}\n\n: keep-alive\n\ndata: {"n":2}\n\n');
		await tick();

		expect(fetcher).toHaveBeenCalledWith('/api/jobs/stream', expect.objectContaining({
			headers: { accept: 'text/event-stream' }
		}));
		expect(h.frames.slice(1)).toEqual([
			{ t: 'open', s: 's1' },
			{ t: 'msg', s: 's1', e: 'message', d: '{"n":1}', id: undefined },
			{ t: 'msg', s: 's1', e: 'message', d: '{"n":2}', id: undefined }
		]);
	});

	it('reports a stream that ends as retryable', async () => {
		const h = track(hub());
		const up = upstream();
		subscribe(h.id, h.owner, 's1', '/api/x/stream', async () => up.response);
		await tick();
		up.end();
		await tick();
		expect(h.frames.at(-1)).toEqual({ t: 'error', s: 's1', fatal: false });
	});

	it('reports an upstream that is not an event stream as fatal', async () => {
		const h = track(hub());
		subscribe(h.id, h.owner, 's1', '/api/x/stream', async () =>
			new Response('nope', { status: 401 })
		);
		await tick();
		expect(h.frames.at(-1)).toEqual({ t: 'error', s: 's1', fatal: true });

		subscribe(h.id, h.owner, 's2', '/api/x/stream', async () =>
			new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } })
		);
		await tick();
		expect(h.frames.at(-1)).toEqual({ t: 'error', s: 's2', fatal: true });
	});

	it('reports a failed upstream fetch as retryable', async () => {
		const h = track(hub());
		subscribe(h.id, h.owner, 's1', '/api/x/stream', async () => {
			throw new Error('ECONNREFUSED');
		});
		await tick();
		expect(h.frames.at(-1)).toEqual({ t: 'error', s: 's1', fatal: false });
	});

	it('cancels the upstream on unsubscribe and sends nothing more for it', async () => {
		const h = track(hub());
		const up = upstream();
		subscribe(h.id, h.owner, 's1', '/api/x/stream', async () => up.response);
		await tick();
		unsubscribe(h.id, h.owner, 's1');
		await tick();
		expect(up.cancelled).toBe(true);
		expect(h.frames.at(-1)).toEqual({ t: 'open', s: 's1' });
	});

	it('cancels every upstream when the connection closes', async () => {
		const h = hub();
		const a = upstream();
		const b = upstream();
		subscribe(h.id, h.owner, 'a', '/api/a/stream', async () => a.response);
		subscribe(h.id, h.owner, 'b', '/api/b/stream', async () => b.response);
		await tick();
		const before = connectionCount();
		closeConnection(h.id);
		await tick();
		expect(a.cancelled && b.cancelled).toBe(true);
		expect(connectionCount()).toBe(before - 1);
	});

	it('refuses subscriptions to an unknown connection or from another user', () => {
		const h = track(hub('user-1'));
		const fetcher = vi.fn();
		expect(subscribe('missing', 'user-1', 's', '/api/x', fetcher)).toBe('unknown-connection');
		expect(subscribe(h.id, 'user-2', 's', '/api/x', fetcher)).toBe('forbidden');
		expect(subscribe(h.id, 'user-1', 's', 'http://evil/api/x', fetcher)).toBe('bad-request');
		expect(fetcher).not.toHaveBeenCalled();
	});

	it('caps the number of streams per connection', () => {
		const h = track(hub());
		const fetcher = () => new Promise<Response>(() => {});
		for (let i = 0; i < MAX_SUBSCRIPTIONS_PER_CONNECTION; i++) {
			expect(subscribe(h.id, h.owner, `s${i}`, '/api/x', fetcher)).toBe('ok');
		}
		expect(subscribe(h.id, h.owner, 'one-more', '/api/x', fetcher)).toBe('too-many');
		// Re-subscribing an existing id replaces it rather than counting twice.
		expect(subscribe(h.id, h.owner, 's0', '/api/x', fetcher)).toBe('ok');
	});

	it('replaces an existing subscription with the same id', async () => {
		const h = track(hub());
		const first = upstream();
		const second = upstream();
		subscribe(h.id, h.owner, 's1', '/api/x', async () => first.response);
		await tick();
		subscribe(h.id, h.owner, 's1', '/api/x', async () => second.response);
		await tick();
		expect(first.cancelled).toBe(true);
		second.send('data: fresh\n\n');
		await tick();
		// The replaced stream winding down must not report an error for the id
		// that now belongs to its replacement.
		expect(h.frames.filter((f) => f.t === 'error')).toEqual([]);
		expect(h.frames.at(-1)).toEqual({ t: 'msg', s: 's1', e: 'message', d: 'fresh', id: undefined });
	});
});
