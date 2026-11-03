import { describe, expect, it } from 'vitest';
import {
	baseName,
	fileActionUrl,
	fileApiUrl,
	fileDownloadUrl,
	joinPath,
	normalizePath,
	parentPath
} from './filePaths';

describe('file manager paths', () => {
	it('joins and splits paths', () => {
		expect(joinPath('/', 'plugins')).toBe('/plugins');
		expect(joinPath('/plugins', 'config.yml')).toBe('/plugins/config.yml');
		expect(parentPath('/plugins/config.yml')).toBe('/plugins');
		expect(parentPath('/plugins')).toBe('/');
		expect(baseName('/plugins/config.yml')).toBe('config.yml');
	});

	it('normalizes typed paths', () => {
		expect(normalizePath('plugins//x.yml/')).toBe('/plugins/x.yml');
		expect(normalizePath('')).toBe('/');
	});

	it('encodes every segment so special names survive', () => {
		expect(fileApiUrl('srv', '/')).toBe('/api/servers/srv/files');
		expect(fileApiUrl('srv', '/my world/a#b?c%d.txt')).toBe(
			'/api/servers/srv/files/my%20world/a%23b%3Fc%25d.txt'
		);
		expect(fileDownloadUrl('srv', '/world')).toBe('/api/servers/srv/files/world?download=true');
		expect(fileActionUrl('srv', 'move')).toBe('/api/servers/srv/file-actions/move');
	});
});
