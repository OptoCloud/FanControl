import type { ServerInit } from '@sveltejs/kit';
import { building } from '$app/environment';
import { getCore } from '$lib/server/core';

// Connect to vigil-core as soon as the server boots, so the first page view already has state.
export const init: ServerInit = () => {
	if (!building) getCore();
};
