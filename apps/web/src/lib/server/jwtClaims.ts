// Kept free of $env imports so it can be unit-tested on its own.

export type TokenClaims = { exp?: number; iat?: number; sub?: string };

/**
 * Reads a JWT's claims WITHOUT verifying it. Only for deciding when to ask the
 * API for a refresh (the API verifies) or for telling one user's requests from
 * another's — never for an access decision.
 */
export function readTokenClaims(token: string): TokenClaims | null {
	const part = token.split('.')[1];
	if (!part) return null;
	try {
		const json = Buffer.from(part, 'base64url').toString('utf8');
		const claims = JSON.parse(json);
		return claims && typeof claims === 'object' ? (claims as TokenClaims) : null;
	} catch {
		return null;
	}
}

/**
 * True once a token is past the middle of its lifetime. A token without `iat`
 * (issued before the API started adding it) is renewed straight away so the
 * next one carries it. An already-expired token is left alone: the API would
 * refuse to refresh it anyway.
 */
export function shouldRefresh(claims: TokenClaims, nowSeconds: number): boolean {
	if (typeof claims.exp !== 'number') return false;
	if (claims.exp <= nowSeconds) return false;
	if (typeof claims.iat !== 'number') return true;
	const lifetime = claims.exp - claims.iat;
	return claims.exp - nowSeconds < lifetime / 2;
}
