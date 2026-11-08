/**
 * Carries every live stream a browser tab has open over ONE connection.
 *
 * Browsers allow at most six concurrent HTTP/1.1 connections per host, shared
 * by every tab, and an EventSource holds one for as long as it is open. MineOS
 * pages open a lot of them: the notification menu keeps two, the dashboard
 * adds two more, a server's logs page three. That left no connection for
 * anything else, so the next fetch or navigation queued behind the streams
 * forever and the page looked frozen until a reload tore them all down — two
 * tabs were enough to hit it on almost any page.
 *
 * Instead the tab opens a single hub stream (GET /api/events) and asks for
 * individual streams with short POSTs. The hub opens each one in-process
 * through the existing /api proxy routes — server-side connections have no
 * such cap — and forwards their events, tagged with the subscription id.
 */
import { createSseParser } from './sseParser';

export type HubFrame =
	| { t: 'hello'; conn: string }
	| { t: 'open'; s: string }
	| { t: 'msg'; s: string; e: string; d: string; id?: string }
	| { t: 'error'; s: string; fatal: boolean };

type Subscription = { abort: AbortController };

type Connection = {
	owner: string;
	send: (frame: HubFrame) => void;
	subs: Map<string, Subscription>;
};

/** Generous for one tab; a ceiling so a misbehaving page cannot pile up upstreams. */
export const MAX_SUBSCRIPTIONS_PER_CONNECTION = 32;

const connections = new Map<string, Connection>();

export function openConnection(owner: string, send: (frame: HubFrame) => void): string {
	const id = crypto.randomUUID();
	connections.set(id, { owner, send, subs: new Map() });
	send({ t: 'hello', conn: id });
	return id;
}

export function closeConnection(id: string) {
	const conn = connections.get(id);
	if (!conn) return;
	connections.delete(id);
	for (const sub of conn.subs.values()) sub.abort.abort();
	conn.subs.clear();
}

export function connectionCount() {
	return connections.size;
}

/**
 * Only same-origin /api paths, and never the hub itself. The stream is fetched
 * in-process with the subscriber's own cookies, so it can reach exactly what a
 * direct EventSource from that browser could — nothing more.
 */
export function isAllowedStreamPath(path: unknown): path is string {
	if (typeof path !== 'string' || path.length > 2048) return false;
	if (!path.startsWith('/api/') || path.startsWith('//')) return false;
	let url: URL;
	try {
		url = new URL(path, 'http://hub.invalid');
	} catch {
		return false;
	}
	if (url.origin !== 'http://hub.invalid') return false;
	if (url.pathname === '/api/events' || url.pathname.startsWith('/api/events/')) return false;
	return url.pathname.startsWith('/api/');
}

export type SubscribeResult = 'ok' | 'unknown-connection' | 'forbidden' | 'too-many' | 'bad-request';

type Fetcher = (input: string, init: RequestInit) => Promise<Response>;

export function subscribe(
	connId: string,
	owner: string,
	subId: string,
	path: string,
	fetcher: Fetcher
): SubscribeResult {
	const conn = connections.get(connId);
	if (!conn) return 'unknown-connection';
	// The connection id is unguessable already; this just keeps one user from
	// steering streams into another user's tab if an id ever leaked.
	if (conn.owner !== owner) return 'forbidden';
	if (typeof subId !== 'string' || subId.length === 0 || subId.length > 64) return 'bad-request';
	if (!isAllowedStreamPath(path)) return 'bad-request';

	conn.subs.get(subId)?.abort.abort();
	if (!conn.subs.has(subId) && conn.subs.size >= MAX_SUBSCRIPTIONS_PER_CONNECTION) {
		return 'too-many';
	}

	const abort = new AbortController();
	const sub: Subscription = { abort };
	conn.subs.set(subId, sub);

	void pump(conn, subId, sub, path, fetcher);
	return 'ok';
}

export function unsubscribe(connId: string, owner: string, subId: string) {
	const conn = connections.get(connId);
	if (!conn || conn.owner !== owner) return;
	const sub = conn.subs.get(subId);
	if (!sub) return;
	conn.subs.delete(subId);
	sub.abort.abort();
}

async function pump(
	conn: Connection,
	subId: string,
	sub: Subscription,
	path: string,
	fetcher: Fetcher
) {
	const live = () => conn.subs.get(subId) === sub && !sub.abort.signal.aborted;
	const finish = (fatal: boolean) => {
		if (!live()) return;
		conn.subs.delete(subId);
		conn.send({ t: 'error', s: subId, fatal });
	};

	let res: Response;
	try {
		res = await fetcher(path, {
			headers: { accept: 'text/event-stream' },
			signal: sub.abort.signal
		});
	} catch {
		finish(false);
		return;
	}

	// Mirrors EventSource: a non-200 or non-stream answer is final, a stream
	// that ends (or drops) is worth retrying.
	const type = res.headers.get('content-type') ?? '';
	if (res.status !== 200 || !type.includes('text/event-stream') || !res.body) {
		res.body?.cancel().catch(() => {});
		finish(true);
		return;
	}

	if (!live()) {
		res.body.cancel().catch(() => {});
		return;
	}
	conn.send({ t: 'open', s: subId });

	const reader = res.body.getReader();
	const onAbort = () => reader.cancel().catch(() => {});
	sub.abort.signal.addEventListener('abort', onAbort, { once: true });

	const decoder = new TextDecoder();
	const parser = createSseParser((ev) => {
		if (live()) conn.send({ t: 'msg', s: subId, e: ev.event, d: ev.data, id: ev.id });
	});

	try {
		while (true) {
			const { done, value } = await reader.read();
			if (done) break;
			parser.push(decoder.decode(value, { stream: true }));
		}
	} catch {
		// Upstream dropped or we aborted it; handled below.
	} finally {
		sub.abort.signal.removeEventListener('abort', onAbort);
	}
	finish(false);
}
