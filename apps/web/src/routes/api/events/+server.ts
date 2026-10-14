import { json } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { closeConnection, openConnection, subscribe, unsubscribe } from '$lib/server/sseHub';
import type { HubFrame } from '$lib/server/sseHub';
import { withHeartbeat } from '$lib/server/sseHeartbeat';
import { AUTH_COOKIE } from '$lib/server/sessionRefresh';
import { readTokenClaims } from '$lib/server/jwtClaims';
import type { Cookies } from '@sveltejs/kit';

// The tab's single live connection; see sseHub.ts for why it exists.

function ownerOf(cookies: Cookies): string | null {
	const token = cookies.get(AUTH_COOKIE);
	if (!token) return null;
	return readTokenClaims(token)?.sub ?? null;
}

export const GET: RequestHandler = ({ cookies, request }) => {
	const owner = ownerOf(cookies);
	if (!owner) return new Response('Not logged in', { status: 401 });

	const encoder = new TextEncoder();
	let connId: string | null = null;
	const cleanup = () => {
		if (connId) closeConnection(connId);
		connId = null;
	};

	const body = new ReadableStream<Uint8Array>({
		start(controller) {
			const send = (frame: HubFrame) => {
				try {
					controller.enqueue(encoder.encode(`data: ${JSON.stringify(frame)}\n\n`));
				} catch {
					// Stream already closed: the client is gone.
					cleanup();
				}
			};
			connId = openConnection(owner, send);
		},
		cancel() {
			cleanup();
		}
	});

	request.signal?.addEventListener('abort', cleanup, { once: true });

	return new Response(withHeartbeat(body, cleanup), {
		headers: {
			'Content-Type': 'text/event-stream',
			'Cache-Control': 'no-cache',
			Connection: 'keep-alive',
			'X-Accel-Buffering': 'no'
		}
	});
};

type HubRequest = {
	action?: unknown;
	conn?: unknown;
	sub?: unknown;
	path?: unknown;
};

const STATUS = {
	ok: 202,
	'unknown-connection': 404,
	forbidden: 403,
	'too-many': 429,
	'bad-request': 400
} as const;

export const POST: RequestHandler = async ({ cookies, request, fetch }) => {
	const owner = ownerOf(cookies);
	if (!owner) return json({ error: 'Not logged in' }, { status: 401 });

	let body: HubRequest;
	try {
		body = await request.json();
	} catch {
		return json({ error: 'Invalid JSON' }, { status: 400 });
	}

	const { action, conn, sub } = body;
	if (typeof conn !== 'string' || typeof sub !== 'string') {
		return json({ error: 'conn and sub are required' }, { status: 400 });
	}

	if (action === 'unsubscribe') {
		unsubscribe(conn, owner, sub);
		return new Response(null, { status: 204 });
	}
	if (action !== 'subscribe') {
		return json({ error: 'Unknown action' }, { status: 400 });
	}

	// `fetch` is this request's event.fetch: the stream is resolved in-process
	// through the normal /api route with this browser's cookies, so the usual
	// proxy (API key + user JWT) and the API's own access checks apply.
	const result = subscribe(conn, owner, sub, body.path as string, (input, init) =>
		fetch(input, init)
	);
	return result === 'ok'
		? new Response(null, { status: STATUS.ok })
		: json({ error: result }, { status: STATUS[result] });
};
