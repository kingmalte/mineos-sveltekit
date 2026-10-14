/**
 * Drop-in stand-in for `EventSource` that shares one connection per tab.
 *
 * Every native EventSource pins one of the browser's six HTTP/1.1 connections
 * to this host — shared by all tabs — for as long as it is open. MineOS pages
 * open enough of them to use up the whole pool, after which every other
 * request queues behind them and the page hangs until it is reloaded. This
 * class keeps the same surface the app uses (`onmessage`, `onerror`, `onopen`,
 * `readyState`, `close()`) but routes the stream through the tab's single hub
 * connection to /api/events (see src/lib/server/sseHub.ts).
 *
 * Semantics follow EventSource: `onerror` fires when the stream drops, and
 * unless `close()` is called the stream reconnects by itself a few seconds
 * later; an answer that is not an event stream (404, 401, ...) closes it for
 * good.
 */

type HubFrame =
	| { t: 'hello'; conn: string }
	| { t: 'open'; s: string }
	| { t: 'msg'; s: string; e: string; d: string; id?: string }
	| { t: 'error'; s: string; fatal: boolean };

const HUB_URL = '/api/events';
const RETRY_MS = 3000;
/** Keep the hub open briefly after the last stream closes; page navigation closes and reopens streams back to back. */
const HUB_IDLE_CLOSE_MS = 5000;

class Hub {
	private source: EventSource | null = null;
	private conn: string | null = null;
	private readonly subs = new Map<string, SharedEventSource>();
	private nextId = 1;
	private reconnectDelay = 1000;
	private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
	private idleTimer: ReturnType<typeof setTimeout> | null = null;

	add(stream: SharedEventSource): string {
		const id = String(this.nextId++);
		this.subs.set(id, stream);
		if (this.idleTimer) {
			clearTimeout(this.idleTimer);
			this.idleTimer = null;
		}
		if (this.conn) this.sendSubscribe(id, stream.url);
		else this.ensureConnected();
		return id;
	}

	/** Re-request a stream that dropped (keeps its id). */
	retry(id: string) {
		const stream = this.subs.get(id);
		if (!stream) return;
		if (this.conn) this.sendSubscribe(id, stream.url);
		else this.ensureConnected();
	}

	remove(id: string) {
		if (!this.subs.delete(id)) return;
		if (this.conn) this.post({ action: 'unsubscribe', conn: this.conn, sub: id });
		if (this.subs.size === 0 && !this.idleTimer) {
			this.idleTimer = setTimeout(() => {
				this.idleTimer = null;
				if (this.subs.size === 0) this.disconnect();
			}, HUB_IDLE_CLOSE_MS);
		}
	}

	private ensureConnected() {
		if (this.source || this.reconnectTimer) return;

		const source = new EventSource(HUB_URL);
		this.source = source;

		source.onmessage = (event) => {
			let frame: HubFrame;
			try {
				frame = JSON.parse(event.data);
			} catch {
				return;
			}
			this.handle(frame);
		};

		source.onerror = () => {
			// The hub itself dropped (proxy restart, network blip, logout): every
			// stream on it is gone. Tell each one, as a native EventSource would,
			// then bring the hub back for whichever are still wanted.
			source.close();
			if (this.source !== source) return;
			this.source = null;
			this.conn = null;
			for (const [id, stream] of [...this.subs]) {
				if (this.subs.get(id) === stream) stream._dropped(false);
			}
			if (this.subs.size > 0) {
				this.reconnectTimer = setTimeout(() => {
					this.reconnectTimer = null;
					if (this.subs.size > 0) this.ensureConnected();
				}, this.reconnectDelay);
				this.reconnectDelay = Math.min(this.reconnectDelay * 2, 15_000);
			}
		};
	}

	private disconnect() {
		this.source?.close();
		this.source = null;
		this.conn = null;
	}

	private handle(frame: HubFrame) {
		if (frame.t === 'hello') {
			this.conn = frame.conn;
			this.reconnectDelay = 1000;
			for (const [id, stream] of this.subs) {
				if (stream._wantsConnection()) this.sendSubscribe(id, stream.url);
			}
			return;
		}
		const stream = this.subs.get(frame.s);
		if (!stream) return;
		if (frame.t === 'open') stream._opened();
		else if (frame.t === 'msg') stream._message(frame.e, frame.d, frame.id);
		else if (frame.t === 'error') stream._dropped(frame.fatal);
	}

	private sendSubscribe(id: string, url: string) {
		const conn = this.conn;
		if (!conn) return;
		this.post({ action: 'subscribe', conn, sub: id, path: url }).then((res) => {
			if (!res || this.conn !== conn || !this.subs.has(id)) return;
			if (res.status === 404) {
				// The server forgot this hub (it restarted); start a fresh one.
				this.source?.onerror?.(new Event('error'));
			} else if (!res.ok) {
				this.subs.get(id)?._dropped(true);
			}
		});
	}

	private async post(body: Record<string, string>): Promise<Response | null> {
		try {
			return await fetch(HUB_URL, {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify(body)
			});
		} catch {
			return null;
		}
	}
}

let hub: Hub | null = null;
const getHub = () => (hub ??= new Hub());

export class SharedEventSource {
	static readonly CONNECTING = 0;
	static readonly OPEN = 1;
	static readonly CLOSED = 2;

	readonly url: string;
	readyState: number = SharedEventSource.CONNECTING;

	onopen: ((event: Event) => void) | null = null;
	onmessage: ((event: MessageEvent) => void) | null = null;
	onerror: ((event: Event) => void) | null = null;

	private id: string;
	private retryTimer: ReturnType<typeof setTimeout> | null = null;

	constructor(url: string) {
		const resolved = new URL(url, window.location.href);
		this.url = resolved.pathname + resolved.search;
		this.id = getHub().add(this);
	}

	close() {
		if (this.readyState === SharedEventSource.CLOSED) return;
		this.readyState = SharedEventSource.CLOSED;
		if (this.retryTimer) {
			clearTimeout(this.retryTimer);
			this.retryTimer = null;
		}
		getHub().remove(this.id);
	}

	/** @internal */
	_wantsConnection() {
		return this.readyState !== SharedEventSource.CLOSED && this.retryTimer === null;
	}

	/** @internal */
	_opened() {
		if (this.readyState === SharedEventSource.CLOSED) return;
		this.readyState = SharedEventSource.OPEN;
		this.onopen?.(new Event('open'));
	}

	/** @internal */
	_message(type: string, data: string, lastEventId?: string) {
		if (this.readyState !== SharedEventSource.OPEN) return;
		// Only unnamed events reach onmessage, as with EventSource; nothing in
		// the app listens for named ones.
		if (type !== 'message') return;
		this.onmessage?.(new MessageEvent('message', { data, lastEventId: lastEventId ?? '' }));
	}

	/** @internal */
	_dropped(fatal: boolean) {
		if (this.readyState === SharedEventSource.CLOSED) return;
		if (fatal) {
			this.readyState = SharedEventSource.CLOSED;
			getHub().remove(this.id);
			this.onerror?.(new Event('error'));
			return;
		}
		this.readyState = SharedEventSource.CONNECTING;
		this.onerror?.(new Event('error'));
		// onerror usually calls close() and reconnects on its own schedule; only
		// retry for callers that left the stream open, like EventSource does.
		if (this.readyState === SharedEventSource.CONNECTING && !this.retryTimer) {
			this.retryTimer = setTimeout(() => {
				this.retryTimer = null;
				if (this.readyState === SharedEventSource.CONNECTING) getHub().retry(this.id);
			}, RETRY_MS);
		}
	}
}
