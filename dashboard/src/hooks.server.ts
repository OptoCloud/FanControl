import type { ServerInit } from '@sveltejs/kit';
import { building } from '$app/environment';
import { getRuntime } from '$lib/server/runtime';

// Connect to the daemon and the database as soon as the server boots, not on the first
// page view: history has to be recorded whether or not anyone is looking.
export const init: ServerInit = () => {
	if (!building) getRuntime();
};
