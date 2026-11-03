import type { Handle } from '@sveltejs/kit';
import { env } from '$env/dynamic/private';
import { isFormContentType, isTrustedOrigin } from '$lib/server/originCheck';
import { renderOriginErrorPage } from '$lib/server/originErrorPage';
import { stripPreloadHeader } from '$lib/server/preloadHeader';
import { AUTH_COOKIE, refreshSessionIfDue } from '$lib/server/sessionRefresh';
import { isPublicApiRoute } from '$lib/server/publicApiRoutes';

// Replaces SvelteKit's csrf.checkOrigin (disabled in svelte.config.js) with a
// dynamic same-origin check so any IP/DNS name pointing at this server works
// without configuring ORIGIN. Scope mirrors SvelteKit's own check: only
// state-changing methods with form-capable content types are validated.
// Trusting X-Forwarded-Host is CSRF-safe: a cross-site attacker cannot attach
// custom headers to a form submission (doing so via fetch forces a CORS
// preflight, which fails), so the header can only come from our own proxy.
const MUTATING_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

function isApiRequest(routeId: string | null, pathname: string): boolean {
	if (routeId?.startsWith('/api/')) return true;
	try {
		return decodeURI(pathname).toLowerCase().startsWith('/api/');
	} catch {
		return true;
	}
}

export const handle: Handle = async ({ event, resolve }) => {
	const { request } = event;

	if (
		MUTATING_METHODS.has(request.method) &&
		isFormContentType(request.headers.get('content-type'))
	) {
		const ctx = {
			origin: request.headers.get('origin'),
			host: request.headers.get('host'),
			forwardedHost: request.headers.get('x-forwarded-host')
		};

		if (!isTrustedOrigin({ ...ctx, configuredOrigin: env.ORIGIN ?? null })) {
			console.warn(
				`[csrf] Blocked ${request.method} ${event.url.pathname}: ` +
					`Origin="${ctx.origin ?? ''}" Host="${ctx.host ?? ''}" ` +
					`X-Forwarded-Host="${ctx.forwardedHost ?? ''}"`
			);
			return new Response(renderOriginErrorPage(ctx), {
				status: 403,
				headers: { 'content-type': 'text/html; charset=utf-8' }
			});
		}
	}

	// Sub-requests (event.fetch from a load) inherit the parent's cookies, so
	// the parent request has already renewed the session if it was due.
	if (!event.isSubRequest) {
		await refreshSessionIfDue(event.cookies, event.url);
	}

	// The /api proxies send the service API key, which the backend treats as
	// admin when no user token comes with it — so a request without a login
	// cookie must never reach them, apart from the few public routes.
	// Checked on the matched route, not the raw path: routing decodes the path,
	// so /%61pi/... reaches the same handlers as /api/....
	if (
		isApiRequest(event.route.id, event.url.pathname) &&
		!event.cookies.get(AUTH_COOKIE) &&
		!isPublicApiRoute(request.method, event.url.pathname)
	) {
		return new Response(JSON.stringify({ error: 'Not logged in' }), {
			status: 401,
			headers: { 'content-type': 'application/json' }
		});
	}

	// Dropped because the preload header does not fit a default reverse-proxy
	// buffer; see preloadHeader.ts for what that broke.
	return stripPreloadHeader(await resolve(event));
};
