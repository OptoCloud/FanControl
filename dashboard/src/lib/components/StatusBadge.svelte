<script lang="ts">
	// Status is never colour alone: every badge carries an icon shape and a text label too.
	interface Props {
		level: 'good' | 'warning' | 'critical' | 'info' | 'unknown';
		label: string;
	}

	let { level, label }: Props = $props();
</script>

<span class="badge {level}">
	<svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true">
		{#if level === 'good'}
			<circle cx="6" cy="6" r="5" />
			<path class="mark" d="M3.5 6.2 5.3 8 8.6 4.4" />
		{:else if level === 'warning'}
			<path d="M6 1 11.5 11H.5Z" />
			<path class="mark" d="M6 4.6v3M6 9.2v.1" />
		{:else if level === 'critical'}
			<rect x="1" y="1" width="10" height="10" rx="2" />
			<path class="mark" d="M4 4l4 4M8 4 4 8" />
		{:else}
			<circle cx="6" cy="6" r="5" />
			<path class="mark" d="M6 5.4v3M6 3.4v.1" />
		{/if}
	</svg>
	{label}
</span>

<style>
	.badge {
		display: inline-flex;
		align-items: center;
		gap: 5px;
		white-space: nowrap;
		color: var(--text-primary);
	}

	svg {
		flex: none;
		fill: var(--text-muted);
	}

	.good svg {
		fill: var(--status-good);
	}

	.warning svg {
		fill: var(--status-warning);
	}

	.critical svg {
		fill: var(--status-critical);
	}

	.mark {
		fill: none;
		stroke: var(--surface);
		stroke-width: 1.5;
		stroke-linecap: round;
		stroke-linejoin: round;
	}

	/* The warning fill is a light yellow, so its mark needs dark ink to stay legible. */
	.warning .mark {
		stroke: #0b0b0b;
	}
</style>
