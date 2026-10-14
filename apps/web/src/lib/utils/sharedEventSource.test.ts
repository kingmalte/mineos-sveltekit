import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/** Stands in for the one real EventSource the hub opens to /api/events. */
class FakeHubSource {
	static instances: FakeHubSource[] = [];
	url: string;
	closed = false;
	onmessage: ((event: { data: string }) => void) | null = null;
	onerror: ((event: Event) => void) | null = null;
	constructor(url: string) {
		this.url = url;
		FakeHubSource.instances.push(this);
	}
	close() {
		this.closed = true;
	}
	frame(frame: object) {
		this.onmessage?.({ data: JSON.stringify(frame) });
	}
	fail() {
		this.onerror?.(new Event('error'));
	}
}

type Posted = { action: string; conn: string; sub: string; path?: string };
let posted: Posted[];
let postStatus: number;

async function load() {
	vi.resetModules();
	return (await import('./sharedEventSource')).SharedEventSource;
}

const hubs = () => FakeHubSource.instances;
const flush = async () => {
	for (let i = 0; i < 5; i++) await Promise.resolve();
};

beforeEach(() => {
	FakeHubSource.instances = [];
	posted = [];
	postStatus = 202;
	vi.useFakeTimers();
	vi.stubGlobal('window', { location: { href: 'http://mineos.test/servers/a' } });
	vi.stubGlobal('EventSource', FakeHubSource);
	vi.stubGlobal(
		'fetch',
		vi.fn(async (_url: string, init: RequestInit) => {
			posted.push(JSON.parse(String(init.body)));
			return new Response(null, { status: postStatus });
		})
	);
});

afterEach(() => {
	vi.unstubAllGlobals();
	vi.useRealTimers();
});

describe('SharedEventSource', () => {
	it('carries every stream over one hub connection', async () => {
		const SharedEventSource = await load();
		const a = new SharedEventSource('/api/notifications/stream');
		const b = new SharedEventSource('/api/jobs/stream');
		const c = new SharedEventSource('/api/servers/a/console/stream?source=java');

		expect(hubs()).toHaveLength(1);
		expect(hubs()[0].url).toBe('/api/events');

		hubs()[0].frame({ t: 'hello', conn: 'c1' });
		await flush();

		expect(posted).toEqual([
			{ action: 'subscribe', conn: 'c1', sub: '1', path: '/api/notifications/stream' },
			{ action: 'subscribe', conn: 'c1', sub: '2', path: '/api/jobs/stream' },
			{ action: 'subscribe', conn: 'c1', sub: '3', path: '/api/servers/a/console/stream?source=java' }
		]);
		expect([a, b, c].every((s) => s.readyState === SharedEventSource.CONNECTING)).toBe(true);
	});

	it('routes open and message frames to the right stream', async () => {
		const SharedEventSource = await load();
		const a = new SharedEventSource('/api/a/stream');
		const b = new SharedEventSource('/api/b/stream');
		const seenA: string[] = [];
		const seenB: string[] = [];
		const onOpen = vi.fn();
		a.onopen = onOpen;
		a.onmessage = (e) => seenA.push(e.data);
		b.onmessage = (e) => seenB.push(e.data);

		const hub = hubs()[0];
		hub.frame({ t: 'hello', conn: 'c1' });
		hub.frame({ t: 'open', s: '1' });
		hub.frame({ t: 'open', s: '2' });
		hub.frame({ t: 'msg', s: '1', e: 'message', d: 'for-a' });
		hub.frame({ t: 'msg', s: '2', e: 'message', d: 'for-b' });
		hub.frame({ t: 'msg', s: '2', e: 'custom', d: 'named-events-are-not-onmessage' });

		expect(onOpen).toHaveBeenCalledTimes(1);
		expect(a.readyState).toBe(SharedEventSource.OPEN);
		expect(seenA).toEqual(['for-a']);
		expect(seenB).toEqual(['for-b']);
	});

	it('subscribes immediately once the hub is already connected', async () => {
		const SharedEventSource = await load();
		new SharedEventSource('/api/a/stream');
		hubs()[0].frame({ t: 'hello', conn: 'c1' });
		new SharedEventSource('/api/b/stream');
		await flush();
		expect(posted.map((p) => p.path)).toEqual(['/api/a/stream', '/api/b/stream']);
		expect(hubs()).toHaveLength(1);
	});

	it('unsubscribes on close and drops the hub once nothing is left', async () => {
		const SharedEventSource = await load();
		const a = new SharedEventSource('/api/a/stream');
		hubs()[0].frame({ t: 'hello', conn: 'c1' });
		a.close();
		await flush();

		expect(a.readyState).toBe(SharedEventSource.CLOSED);
		expect(posted.at(-1)).toEqual({ action: 'unsubscribe', conn: 'c1', sub: '1' });
		expect(hubs()[0].closed).toBe(false);
		vi.advanceTimersByTime(5000);
		expect(hubs()[0].closed).toBe(true);
	});

	it('keeps the hub when a new stream arrives right after the last one closed', async () => {
		const SharedEventSource = await load();
		new SharedEventSource('/api/a/stream').close();
		new SharedEventSource('/api/b/stream');
		vi.advanceTimersByTime(10_000);
		expect(hubs()).toHaveLength(1);
		expect(hubs()[0].closed).toBe(false);
	});

	it('a fatal error closes the stream for good', async () => {
		const SharedEventSource = await load();
		const a = new SharedEventSource('/api/a/stream');
		const onError = vi.fn();
		a.onerror = onError;
		hubs()[0].frame({ t: 'hello', conn: 'c1' });
		hubs()[0].frame({ t: 'error', s: '1', fatal: true });

		expect(onError).toHaveBeenCalledTimes(1);
		expect(a.readyState).toBe(SharedEventSource.CLOSED);
		vi.advanceTimersByTime(10_000);
		await flush();
		expect(posted.filter((p) => p.action === 'subscribe')).toHaveLength(1);
	});

	it('a dropped stream retries on its own unless onerror closes it', async () => {
		const SharedEventSource = await load();
		const kept = new SharedEventSource('/api/kept/stream');
		const closed = new SharedEventSource('/api/closed/stream');
		closed.onerror = () => closed.close();
		const keptErrors = vi.fn();
		kept.onerror = keptErrors;

		const hub = hubs()[0];
		hub.frame({ t: 'hello', conn: 'c1' });
		hub.frame({ t: 'error', s: '1', fatal: false });
		hub.frame({ t: 'error', s: '2', fatal: false });
		expect(keptErrors).toHaveBeenCalledTimes(1);
		expect(kept.readyState).toBe(SharedEventSource.CONNECTING);

		posted = [];
		vi.advanceTimersByTime(3000);
		await flush();
		expect(posted.filter((p) => p.action === 'subscribe')).toEqual([
			{ action: 'subscribe', conn: 'c1', sub: '1', path: '/api/kept/stream' }
		]);
	});

	it('when the hub drops, every stream errors and the open ones come back on a new hub', async () => {
		const SharedEventSource = await load();
		const a = new SharedEventSource('/api/a/stream');
		const b = new SharedEventSource('/api/b/stream');
		const aErrors = vi.fn();
		a.onerror = aErrors;
		b.onerror = () => b.close();

		hubs()[0].frame({ t: 'hello', conn: 'c1' });
		hubs()[0].fail();
		expect(hubs()[0].closed).toBe(true);
		expect(aErrors).toHaveBeenCalledTimes(1);
		expect(b.readyState).toBe(SharedEventSource.CLOSED);

		vi.advanceTimersByTime(1000);
		expect(hubs()).toHaveLength(2);
		posted = [];
		vi.advanceTimersByTime(3000); // a's own retry timer
		hubs()[1].frame({ t: 'hello', conn: 'c2' });
		await flush();
		expect(posted).toEqual([{ action: 'subscribe', conn: 'c2', sub: '1', path: '/api/a/stream' }]);
	});

	it('starts a fresh hub when the server no longer knows the connection', async () => {
		const SharedEventSource = await load();
		new SharedEventSource('/api/a/stream');
		postStatus = 404;
		hubs()[0].frame({ t: 'hello', conn: 'stale' });
		await flush();
		expect(hubs()[0].closed).toBe(true);
		vi.advanceTimersByTime(1000);
		expect(hubs()).toHaveLength(2);
	});

	it('closes a stream the server refuses to subscribe', async () => {
		const SharedEventSource = await load();
		const a = new SharedEventSource('/api/a/stream');
		const onError = vi.fn();
		a.onerror = onError;
		postStatus = 401;
		hubs()[0].frame({ t: 'hello', conn: 'c1' });
		await flush();
		expect(onError).toHaveBeenCalledTimes(1);
		expect(a.readyState).toBe(SharedEventSource.CLOSED);
	});
});
