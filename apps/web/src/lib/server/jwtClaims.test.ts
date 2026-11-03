import { describe, expect, it } from 'vitest';
import { readTokenClaims, shouldRefresh } from './jwtClaims';

const token = (claims: object) =>
	`header.${Buffer.from(JSON.stringify(claims)).toString('base64url')}.signature`;

describe('readTokenClaims', () => {
	it('reads the payload of a JWT', () => {
		expect(readTokenClaims(token({ sub: '7', exp: 100, iat: 40 }))).toEqual({
			sub: '7',
			exp: 100,
			iat: 40
		});
	});

	it('returns null for anything that is not a JWT', () => {
		expect(readTokenClaims('not-a-jwt')).toBeNull();
		expect(readTokenClaims('a.%%%.c')).toBeNull();
		expect(readTokenClaims(`a.${Buffer.from('"str"').toString('base64url')}.c`)).toBeNull();
	});
});

describe('shouldRefresh', () => {
	const iat = 1_000;
	const exp = iat + 3600;

	it('leaves a token alone during the first half of its lifetime', () => {
		expect(shouldRefresh({ iat, exp }, iat + 10)).toBe(false);
		expect(shouldRefresh({ iat, exp }, iat + 1799)).toBe(false);
	});

	it('renews a token past the middle of its lifetime', () => {
		expect(shouldRefresh({ iat, exp }, iat + 1801)).toBe(true);
		expect(shouldRefresh({ iat, exp }, exp - 1)).toBe(true);
	});

	it('does not try to revive an expired token', () => {
		expect(shouldRefresh({ iat, exp }, exp)).toBe(false);
		expect(shouldRefresh({ iat, exp }, exp + 60)).toBe(false);
	});

	it('renews a token issued before iat was added, so the next one carries it', () => {
		expect(shouldRefresh({ exp }, iat)).toBe(true);
	});

	it('ignores a token without an expiry', () => {
		expect(shouldRefresh({ iat }, iat)).toBe(false);
	});
});
