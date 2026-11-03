/**
 * Paths in the server file manager. Paths are absolute within the server
 * directory ("/", "/plugins/config.yml") and are encoded segment by segment when
 * they become URLs, so names with `#`, `?`, `%` or spaces reach the API intact.
 */

export function joinPath(dir: string, name: string): string {
	const trimmed = name.replace(/^\/+/, '');
	return dir === '/' || dir === '' ? `/${trimmed}` : `${dir}/${trimmed}`;
}

export function parentPath(path: string): string {
	const parts = path.split('/').filter((p) => p !== '');
	parts.pop();
	return parts.length === 0 ? '/' : `/${parts.join('/')}`;
}

export function baseName(path: string): string {
	const parts = path.split('/').filter((p) => p !== '');
	return parts[parts.length - 1] ?? '';
}

/** Makes user input like "plugins/x.yml" or "/plugins//x.yml" a clean absolute path. */
export function normalizePath(path: string): string {
	const parts = path.split('/').filter((p) => p !== '');
	return parts.length === 0 ? '/' : `/${parts.join('/')}`;
}

function encodePath(path: string): string {
	return path
		.split('/')
		.map((segment) => encodeURIComponent(segment))
		.join('/');
}

export function fileApiUrl(server: string, path: string): string {
	const base = `/api/servers/${encodeURIComponent(server)}/files`;
	return path === '/' || path === '' ? base : `${base}${encodePath(path)}`;
}

export function fileDownloadUrl(server: string, path: string): string {
	return `${fileApiUrl(server, path)}?download=true`;
}

export function fileActionUrl(
	server: string,
	action: 'create-file' | 'create-folder' | 'move' | 'copy'
): string {
	return `/api/servers/${encodeURIComponent(server)}/file-actions/${action}`;
}
