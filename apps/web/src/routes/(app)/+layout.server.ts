import { error, redirect } from '@sveltejs/kit';
import type { LayoutServerLoad } from './$types';
import * as api from '$lib/api/client';

export const load: LayoutServerLoad = async ({ cookies, fetch, url }) => {
	const token = cookies.get('auth_token');

	if (!token) {
		throw redirect(303, '/login');
	}

	let meResponse: Response;
	try {
		meResponse = await fetch('/api/auth/me');
	} catch {
		// The API is unreachable (restarting, network blip). That says nothing
		// about the session, so show an error the user can retry instead of
		// sending them to /login — which bounces a still-valid cookie straight
		// back here and loops.
		throw error(503, 'The MineOS API is not reachable right now. Please try again in a moment.');
	}

	if (meResponse.status === 401 || meResponse.status === 403) {
		throw redirect(303, '/login');
	}
	if (!meResponse.ok) {
		throw error(503, 'The MineOS API is not reachable right now. Please try again in a moment.');
	}

	const user = await meResponse.json().catch(() => null);
	if (!user) {
		throw error(503, 'The MineOS API returned an unexpected response.');
	}

	if (url.pathname.startsWith('/profiles/buildtools')) {
		console.info('[layout] buildtools load', url.pathname, url.search);
	}

	// Load servers and profiles for search
	const [servers, profiles] = await Promise.all([
		api.getAllServers(fetch),
		api.getHostProfiles(fetch)
	]);

	return {
		user,
		servers: servers.data ?? [],
		profiles: profiles.data ?? []
	};
};
