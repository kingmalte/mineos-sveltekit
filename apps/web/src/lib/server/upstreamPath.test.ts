import { describe, expect, it } from 'vitest';
import { encodePathSegments } from './upstreamPath';

describe('encodePathSegments', () => {
	it('leaves ordinary paths alone', () => {
		expect(encodePathSegments('srv/files/plugins/config.yml')).toBe('srv/files/plugins/config.yml');
	});

	it('encodes characters that would end or re-route the path', () => {
		expect(encodePathSegments('srv/files/a#b?c%d.txt')).toBe('srv/files/a%23b%3Fc%25d.txt');
	});

	it('encodes spaces and keeps blank segments as names', () => {
		expect(encodePathSegments('srv/files/my world/ ')).toBe('srv/files/my%20world/%20');
	});
});
