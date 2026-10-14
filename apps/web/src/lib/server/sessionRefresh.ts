import { env } from '$env/dynamic/private';
import type { Cookies } from '@sveltejs/kit';
import { readTokenClaims, shouldRefresh } from './jwtClaims';

/**
 * Keeps an active session alive past its token's lifetime.
 *
 * The API issues a JWT that expires after `Auth:Jwt:ExpiresMinutes` (60 by
 * default) and the login cookie expires with it. Nothing ever renewed it, so
 * every user was thrown back to the login page once an hour no matter how
 * busy they were — mid-command, mid-upload. Now any request that arrives with
 * a token past the middle of its lifetime trades it for a fresh one, so the
 * session only ends after a full token lifetime without any requests. An open
 * tab checks in every few minutes (see routes/(app)/+layout.svelte), so in
 * practice it ends when the user logs out or closes MineOS for that long.
 */

const baseUrl = env.PRIVATE_API_BASE_URL ?? env.INTERNAL_API_URL ?? 'http://api:5078';

export const AUTH_COOKIE = 'auth_token';

type RefreshResult = { accessToken: string; expiresInSeconds: number } | 'rejected' | null;

// A page load fires several requests at once; they should share one refresh.
const inFlight = new Map<string, Promise<RefreshResult>>();

async function requestRefresh(token: string): Promise<RefreshResult> {
	try {
		const res = await fetch(`${baseUrl}/api/v1/auth/refresh`, {
			method: 'POST',
			headers: { Authorization: `Bearer ${token}` }
		});
		if (res.status === 401) return 'rejected';
		if (!res.ok) return null;
		const body = await res.json();
		if (typeof body?.accessToken !== 'string' || typeof body?.expiresInSeconds !== 'number') {
			return null;
		}
		return { accessToken: body.accessToken, expiresInSeconds: body.expiresInSeconds };
	} catch {
		// API briefly unreachable — keep the current token and try again on the
		// next request; it is still valid.
		return null;
	}
}

function refreshOnce(token: string): Promise<RefreshResult> {
	let pending = inFlight.get(token);
	if (!pending) {
		pending = requestRefresh(token).finally(() => {
			// Keep the result briefly so requests that were already on their way
			// with the old token reuse it instead of refreshing again.
			setTimeout(() => inFlight.delete(token), 10_000);
		});
		inFlight.set(token, pending);
	}
	return pending;
}

export function setAuthCookie(cookies: Cookies, url: URL, token: string, maxAge: number) {
	cookies.set(AUTH_COOKIE, token, {
		httpOnly: true,
		secure: url.protocol === 'https:',
		sameSite: 'lax',
		maxAge,
		path: '/'
	});
}

/**
 * Renews the request's auth cookie when it is due. Updates `cookies` in place,
 * so handlers further down the chain already forward the new token.
 */
export async function refreshSessionIfDue(cookies: Cookies, url: URL): Promise<void> {
	const token = cookies.get(AUTH_COOKIE);
	if (!token) return;

	const claims = readTokenClaims(token);
	if (!claims || !shouldRefresh(claims, Date.now() / 1000)) return;

	const result = await refreshOnce(token);
	if (result === 'rejected') {
		// The user was deleted or deactivated: end the session now rather than
		// letting it run out on its own.
		cookies.delete(AUTH_COOKIE, { path: '/' });
		return;
	}
	if (result) {
		setAuthCookie(cookies, url, result.accessToken, result.expiresInSeconds);
	}
}
