<script lang="ts">
	import { relativeTime } from '$lib/chart/scale';
	import type { EventRecord } from '$lib/types';
	import StatusBadge from './StatusBadge.svelte';

	let { events, now }: { events: EventRecord[]; now: number } = $props();

	const label = { critical: 'Critical', warning: 'Warning', info: 'Info' } as const;
</script>

<section class="card">
	<h2 class="card-title">Events</h2>
	<ul>
		{#each events as event (event.id)}
			<li>
				<span class="severity"><StatusBadge level={event.severity} label={label[event.severity]} /></span>
				<!-- Messages come from the server's own templates, but they embed device names; text interpolation keeps them inert. -->
				<span class="message">{event.message}</span>
				<time class="muted numeric" datetime={event.ts} title={new Date(event.ts).toLocaleString()}>{relativeTime(event.ts, now)}</time>
			</li>
		{:else}
			<li class="muted">Nothing has happened yet. Stalled fans, unreadable sensors, SMART changes and daemon outages show up here.</li>
		{/each}
	</ul>
</section>

<style>
	ul {
		list-style: none;
		margin: 8px 0 0;
		padding: 0;
		max-height: 320px;
		overflow-y: auto;
	}

	li {
		display: grid;
		grid-template-columns: 84px 1fr auto;
		gap: 12px;
		padding: 7px 0;
		border-bottom: 1px solid var(--grid);
		align-items: start;
	}

	li:last-child {
		border-bottom: 0;
	}

	li.muted {
		display: block;
	}

	.severity {
		font-size: 12px;
		padding-top: 1px;
	}

	time {
		font-size: 12px;
		white-space: nowrap;
	}
</style>
