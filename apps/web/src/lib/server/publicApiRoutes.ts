/**
 * The /api routes a browser may call without being logged in.
 *
 * Every /api proxy attaches the service API key, which the backend treats as a
 * full admin identity whenever no user token comes with it. Before this gate an
 * anonymous visitor could therefore do anything through the web app — list
 * users, create an admin account — just by calling /api/... without a cookie.
 * hooks.server.ts now turns away any /api request without a login cookie
 * unless it is listed here.
 *
 * Keep this list short and exact. Anything on it is served with admin rights.
 */

/** Characters that, once decoded, could move a request to a different upstream path. */
const UNSAFE_IN_SEGMENT = /[/\\?#]/;

/** Decodes one path segment, refusing anything that could re-route the proxy. */
function safeSegment(raw: string): string | null {
	let decoded: string;
	try {
		decoded = decodeURIComponent(raw);
	} catch {
		return null;
	}
	if (decoded === '' || decoded === '.' || decoded === '..') return null;
	if (UNSAFE_IN_SEGMENT.test(decoded)) return null;
	return decoded;
}

export function isPublicApiRoute(method: string, pathname: string): boolean {
	const m = method.toUpperCase();

	// Logging in, and logging out (which only clears cookies).
	if (m === 'POST' && (pathname === '/api/auth/login' || pathname === '/api/auth/logout')) {
		return true;
	}

	// Client-pack download links handed to players, who have no MineOS account:
	// /api/servers/{server}/client-packages/{file}/download (see /client-packages).
	if (m === 'GET' || m === 'HEAD') {
		const parts = pathname.split('/');
		// ['', 'api', 'servers', server, 'client-packages', file, 'download']
		if (
			parts.length === 7 &&
			parts[1] === 'api' &&
			parts[2] === 'servers' &&
			parts[4] === 'client-packages' &&
			parts[6] === 'download' &&
			safeSegment(parts[3]) !== null &&
			safeSegment(parts[5]) !== null
		) {
			return true;
		}
	}

	return false;
}
