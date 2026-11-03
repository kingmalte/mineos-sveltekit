/**
 * Requests for the server mods page. Every failure carries the API's own message:
 * the page used to show "Upload failed: Bad Request" (the HTTP status text) or,
 * for a failed enable/disable, nothing at all.
 */

export const enc = encodeURIComponent;

export function modsUrl(server: string, path = ''): string {
	return `/api/servers/${enc(server)}${path}`;
}

/** The API's error text from a failed response, or `fallback`. */
export async function errorMessage(res: Response, fallback: string): Promise<string> {
	const body = await res
		.clone()
		.json()
		.catch(() => null);
	if (body && typeof body === 'object') {
		const text = body.error ?? body.detail ?? body.message ?? body.title;
		if (typeof text === 'string' && text.trim()) return text;
	}
	return fallback;
}

/** fetch + JSON with the API's error message on failure. */
export async function requestJson<T>(
	url: string,
	init: RequestInit & { json?: unknown } = {},
	fallback = 'The request failed'
): Promise<T> {
	const { json, ...rest } = init;
	const res = await fetch(url, {
		...rest,
		headers: json !== undefined ? { 'Content-Type': 'application/json', ...rest.headers } : rest.headers,
		body: json !== undefined ? JSON.stringify(json) : rest.body
	});
	if (!res.ok) throw new Error(await errorMessage(res, `${fallback} (${res.status})`));
	const text = await res.text();
	return (text ? JSON.parse(text) : null) as T;
}

/** Multipart upload with progress; rejects with the API's error message. */
export function uploadWithProgress<T = unknown>(
	url: string,
	file: File,
	onProgress: (percent: number) => void
): Promise<T> {
	return new Promise((resolve, reject) => {
		const form = new FormData();
		form.append('file', file);
		const xhr = new XMLHttpRequest();
		xhr.upload.addEventListener('progress', (e) => {
			if (e.lengthComputable) onProgress(Math.round((e.loaded / e.total) * 100));
		});
		xhr.addEventListener('load', () => {
			let body: any = null;
			try {
				body = xhr.responseText ? JSON.parse(xhr.responseText) : null;
			} catch {
				body = null;
			}
			if (xhr.status >= 200 && xhr.status < 300) {
				resolve(body as T);
			} else {
				const message =
					(body && (body.error || body.detail || body.message)) ||
					`Upload of ${file.name} failed (${xhr.status} ${xhr.statusText})`;
				reject(new Error(message));
			}
		});
		xhr.addEventListener('error', () => reject(new Error(`Upload of ${file.name} failed - connection lost`)));
		xhr.addEventListener('abort', () => reject(new Error('Upload cancelled')));
		xhr.open('POST', url);
		xhr.send(form);
	});
}

export type LoaderInfo = {
	loader: string | null;
	version: string | null;
	minecraftVersion: string | null;
	manual: boolean;
};

export type ModInfo = {
	fileName: string;
	isDisabled: boolean;
	modId: string | null;
	name: string | null;
	version: string | null;
	platform: string | null;
	environment: 'both' | 'client' | 'server';
};

export type ModIssue = {
	severity: 'error' | 'warning';
	kind: 'client-only' | 'missing-dependency' | 'duplicate' | 'wrong-loader';
	message: string;
	fileNames: string[];
};

export type ModCheckResult = {
	serverLoader: string | null;
	minecraftVersion: string | null;
	mods: ModInfo[];
	issues: ModIssue[];
};

export type ModUpdate = {
	fileName: string;
	projectId: string;
	currentVersion: string;
	latestVersionId: string;
	latestVersion: string;
	latestFileName: string;
	published: string;
};

export type ModUpdateCheck = {
	loader: string | null;
	minecraftVersion: string | null;
	modsChecked: number;
	recognizedOnModrinth: number;
	updates: ModUpdate[];
};

export const loaderNames: Record<string, string> = {
	forge: 'Forge',
	fabric: 'Fabric',
	neoforge: 'NeoForge',
	quilt: 'Quilt'
};

export const loaderLogos: Record<string, string> = {
	forge: '/images/loaders/forge.png',
	fabric: '/images/loaders/fabric.png',
	neoforge: '/images/loaders/neoforge.png',
	quilt: '/images/loaders/quilt.svg'
};
