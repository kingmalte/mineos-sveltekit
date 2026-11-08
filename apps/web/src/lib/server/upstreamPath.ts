/**
 * Turns a decoded SvelteKit rest param (`[...path]`) back into a URL path for the
 * upstream request.
 *
 * SvelteKit hands route params over decoded, so a file named `a#b.txt` arrives as
 * `a#b.txt`. Pasted into the upstream URL as-is, everything from `#` on became a
 * fragment and `?` started a query string — the API was asked for a different
 * file, or none. Each segment is encoded again; `/` stays the separator.
 */
export function encodePathSegments(path: string): string {
	return path
		.split('/')
		.map((segment) => encodeURIComponent(segment))
		.join('/');
}
