import { describe, expect, it } from 'vitest';
import { isPublicApiRoute } from './publicApiRoutes';

describe('isPublicApiRoute', () => {
	it('lets an anonymous browser log in and out', () => {
		expect(isPublicApiRoute('POST', '/api/auth/login')).toBe(true);
		expect(isPublicApiRoute('POST', '/api/auth/logout')).toBe(true);
	});

	it('allows the shared client-pack download link', () => {
		expect(isPublicApiRoute('GET', '/api/servers/survival/client-packages/pack-1.mrpack/download')).toBe(
			true
		);
		expect(
			isPublicApiRoute('GET', '/api/servers/my%20server/client-packages/My%20Pack%20(1).zip/download')
		).toBe(true);
	});

	it.each([
		['GET', '/api/auth/users'],
		['POST', '/api/auth/users'],
		['GET', '/api/auth/me'],
		['GET', '/api/auth/login'],
		['GET', '/api/host/servers'],
		['GET', '/api/settings'],
		['GET', '/api/servers'],
		['POST', '/api/servers/survival/start'],
		['GET', '/api/servers/survival/client-packages'],
		['DELETE', '/api/servers/survival/client-packages/pack.zip/download'],
		['POST', '/api/servers/survival/client-packages/pack.zip/download'],
		['GET', '/api/servers/survival/client-packages/pack.zip/download/extra'],
		['GET', '/api/events']
	])('refuses %s %s', (method, path) => {
		expect(isPublicApiRoute(method, path)).toBe(false);
	});

	it.each([
		// An encoded slash or query mark in a segment would re-route the upstream
		// request once the proxy decodes it, e.g. to /api/v1/auth/users.
		'/api/servers/..%2Fauth%2Fusers%3F/client-packages/x/download',
		'/api/servers/x/client-packages/..%2F..%2Fsettings%3F/download',
		'/api/servers/%2E%2E/client-packages/x/download',
		'/api/servers/../client-packages/x/download',
		'/api/servers/x/client-packages/a%5Cb/download',
		'/api/servers/x/client-packages/a%23b/download',
		'/api/servers//client-packages/x/download',
		'/api/servers/%E0%A4%A/client-packages/x/download'
	])('refuses a download path that could escape its route: %s', (path) => {
		expect(isPublicApiRoute('GET', path)).toBe(false);
	});
});
