<script lang="ts">
	import { linearScale } from '$lib/chart/scale';
	import type { SeriesPoints } from '$lib/types';

	interface Props {
		label: string;
		/** null: the sensor is currently unreadable. */
		celsius: number | null;
		/** History for the selected range, drawn as a trend line and summarised as a min/max. */
		points: SeriesPoints;
		detail?: string;
	}

	let { label, celsius, points, detail }: Props = $props();

	const WIDTH = 120;
	const HEIGHT = 32;

	const range = $derived.by(() => {
		if (points.length === 0) return null;
		const values = points.map((point) => point[1]);
		return { min: Math.min(...values), max: Math.max(...values) };
	});

	const trend = $derived.by(() => {
		if (!range || points.length < 2) return null;
		// At least a few degrees of headroom, so a flat 38.0-38.2 doesn't get drawn as a mountain range.
		const padding = Math.max(0, (4 - (range.max - range.min)) / 2);
		const x = linearScale([points[0][0], points[points.length - 1][0]], [2, WIDTH - 4]);
		const y = linearScale([range.min - padding, range.max + padding], [HEIGHT - 3, 3]);
		const last = points[points.length - 1];
		return {
			path: points.map(([time, value], i) => `${i === 0 ? 'M' : 'L'}${x(time).toFixed(1)},${y(value).toFixed(1)}`).join(''),
			end: { x: x(last[0]), y: y(last[1]) }
		};
	});
</script>

<div class="card tile">
	<div class="label">{label}</div>
	<div class="main">
		<!-- Proportional figures on purpose: tabular digits make a large standalone number look loose. -->
		<div class="value">
			{#if celsius === null}
				<span class="unavailable">n/a</span>
			{:else}
				{celsius.toFixed(celsius >= 100 ? 0 : 1)}<span class="unit">°C</span>
			{/if}
		</div>
		{#if trend}
			<svg width={WIDTH} height={HEIGHT} aria-hidden="true">
				<path d={trend.path} fill="none" stroke="var(--text-muted)" stroke-width="1.5" stroke-linejoin="round" stroke-linecap="round" />
				<circle cx={trend.end.x} cy={trend.end.y} r="3" fill="var(--series-1)" stroke="var(--surface)" stroke-width="1.5" />
			</svg>
		{/if}
	</div>
	<div class="detail muted numeric">
		{#if detail}{detail}{:else if range}{range.min.toFixed(0)} to {range.max.toFixed(0)}°C in range{:else}&nbsp;{/if}
	</div>
</div>

<style>
	.tile {
		display: flex;
		flex-direction: column;
		gap: 2px;
		padding: 12px 14px;
	}

	.label {
		font-size: 12px;
		color: var(--text-secondary);
	}

	.main {
		display: flex;
		align-items: center;
		justify-content: space-between;
		gap: 8px;
	}

	.value {
		font-size: 26px;
		font-weight: 600;
		line-height: 1.15;
		white-space: nowrap;
	}

	.unit {
		font-size: 14px;
		font-weight: 400;
		color: var(--text-secondary);
		margin-left: 2px;
	}

	.unavailable {
		color: var(--text-muted);
	}

	.detail {
		font-size: 11px;
	}

	svg {
		flex: none;
	}
</style>
