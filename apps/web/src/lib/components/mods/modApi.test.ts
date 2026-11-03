import { afterEach, describe, expect, it, vi } from 'vitest';
import { errorMessage, modsUrl, requestJson } from './modApi';

describe('mods page requests', () => {
	afterEach(() => vi.unstubAllGlobals());

	it('prefers the API error text over the HTTP status', async () => {
		const res = new Response(JSON.stringify({ error: 'x.jar.disabled already exists' }), { status: 409 });
		expect(await errorMessage(res, 'fallback')).toBe('x.jar.disabled already exists');
	});

	it('falls back when the body is not JSON', async () => {
		expect(await errorMessage(new Response('<html>', { status: 502 }), 'fallback')).toBe('fallback');
	});

	it('throws the API message from requestJson', async () => {
		vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ error: 'nope' }), { status: 409 })));
		await expect(requestJson('/x', { method: 'POST' })).rejects.toThrow('nope');
	});

	it('sends JSON bodies and parses empty answers', async () => {
		const fetchMock = vi.fn(async () => new Response('', { status: 200 }));
		vi.stubGlobal('fetch', fetchMock);
		expect(await requestJson('/x', { method: 'POST', json: { a: 1 } })).toBeNull();
		const init = (fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1];
		expect(init.body).toBe('{"a":1}');
		expect((init.headers as Record<string, string>)['Content-Type']).toBe('application/json');
	});

	it('encodes server names', () => {
		expect(modsUrl('my server', '/mods')).toBe('/api/servers/my%20server/mods');
	});
});
