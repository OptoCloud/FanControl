<script lang="ts">
	import { formatInstant, formatTick, linearScale, nearestIndex, niceTicks, timeTicks } from '$lib/chart/scale';
	import type { ChartSeries, SeriesPoints } from '$lib/types';

	interface Props {
		title: string;
		series: ChartSeries[];
		from: number;
		to: number;
		unit: string;
		/** Fixes the y-axis (e.g. 0-100 for duty). Omitted: fitted to the data with round bounds. */
		yDomain?: [number, number];
		loading?: boolean;
	}

	let { title, series, from, to, unit, yDomain, loading = false }: Props = $props();

	const HEIGHT = 240;
	const MARGIN = { top: 12, right: 16, bottom: 26, left: 40 };

	let width = $state(640);
	let hidden = $state(new Set<string>());
	let hoverTime = $state<number | null>(null);
	let showTable = $state(false);

	const visible = $derived(series.filter((s) => !hidden.has(s.id) && s.points.length > 0));
	const span = $derived(to - from);

	const yBounds = $derived.by((): [number, number] => {
		if (yDomain) return yDomain;
		const values = visible.flatMap((s) => s.points.map((point) => point[1]));
		if (values.length === 0) return [0, 1];
		const ticks = niceTicks(Math.min(...values), Math.max(...values), 4);
		return [ticks[0], ticks[ticks.length - 1]];
	});

	const x = $derived(linearScale([from, to], [MARGIN.left, Math.max(MARGIN.left + 1, width - MARGIN.right)]));
	const y = $derived(linearScale(yBounds, [HEIGHT - MARGIN.bottom, MARGIN.top]));
	const yTicks = $derived(niceTicks(yBounds[0], yBounds[1], 4).filter((tick) => tick >= yBounds[0] && tick <= yBounds[1]));
	const xTicks = $derived(timeTicks(from, to, Math.max(2, Math.floor((width - MARGIN.left - MARGIN.right) / 90))));

	function path(points: SeriesPoints): string {
		// A gap several buckets wide means the data really is missing (the app was down);
		// bridging it with a straight line would invent readings.
		const gap = medianStep(points) * 4;
		let d = '';
		for (let i = 0; i < points.length; i++) {
			const [time, value] = points[i];
			const broken = i === 0 || time - points[i - 1][0] > gap;
			d += `${broken ? 'M' : 'L'}${x(time).toFixed(1)},${y(value).toFixed(1)}`;
		}
		return d;
	}

	function medianStep(points: SeriesPoints): number {
		if (points.length < 3) return Infinity;
		const steps = points.slice(1).map((point, i) => point[0] - points[i][0]).sort((a, b) => a - b);
		return steps[steps.length >> 1];
	}

	// The crosshair finds the X: it snaps to the nearest sample of the densest visible series,
	// and the readout then lists EVERY series there, so the pointer never has to land on a line.
	const readout = $derived.by(() => {
		if (hoverTime === null || visible.length === 0) return null;
		const reference = visible.reduce((a, b) => (b.points.length > a.points.length ? b : a));
		const time = reference.points[nearestIndex(reference.points, hoverTime)][0];

		const rows = visible.flatMap((s) => {
			const point = s.points[nearestIndex(s.points, time)];
			// Don't report a value from far away as if it belonged to this instant.
			return Math.abs(point[0] - time) <= span / 50 ? [{ series: s, value: point[1] }] : [];
		});
		return { time, rows };
	});

	const latest = $derived(new Map(series.map((s) => [s.id, s.points.at(-1)?.[1]])));

	function pointerMove(event: PointerEvent) {
		const bounds = (event.currentTarget as SVGElement).getBoundingClientRect();
		hoverTime = Math.min(to, Math.max(from, x.invert(event.clientX - bounds.left)));
	}

	function keyDown(event: KeyboardEvent) {
		if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
		event.preventDefault();
		const step = (span / 60) * (event.key === 'ArrowLeft' ? -1 : 1);
		hoverTime = Math.min(to, Math.max(from, (hoverTime ?? to) + step));
	}

	function toggle(id: string) {
		const next = new Set(hidden);
		if (!next.delete(id)) next.add(id);
		hidden = next;
	}

	const format = (value: number | undefined) => (value === undefined ? 'n/a' : `${Math.round(value * 10) / 10}${unit}`);

	// The table view is the chart's accessible twin: the same data, thinned to a readable number of rows.
	const tableRows = $derived.by(() => {
		const reference = visible.reduce<ChartSeries | null>((a, b) => (!a || b.points.length > a.points.length ? b : a), null);
		if (!reference) return [];
		const stride = Math.max(1, Math.ceil(reference.points.length / 24));
		return reference.points
			.filter((_, i) => i % stride === 0)
			.map(([time]) => ({ time, values: visible.map((s) => s.points[nearestIndex(s.points, time)]?.[1]) }));
	});

	const tooltipLeft = $derived(readout ? Math.min(Math.max(x(readout.time) + 12, MARGIN.left), width - 190) : 0);
</script>

<figure class="card chart">
	<figcaption>
		<h2 class="card-title">{title}</h2>
		<button class="table-toggle" type="button" aria-pressed={showTable} onclick={() => (showTable = !showTable)}>
			{showTable ? 'Show chart' : 'Show table'}
		</button>
	</figcaption>

	<!-- Always present for several series: identity never rests on colour-matching alone. Click to hide a series. -->
	<ul class="legend">
		{#each series as s (s.id)}
			<li>
				<button type="button" class:off={hidden.has(s.id)} aria-pressed={!hidden.has(s.id)} onclick={() => toggle(s.id)}>
					<span class="key" style:background={s.color}></span>
					<span class="name">{s.label}</span>
					<span class="value numeric">{format(latest.get(s.id))}</span>
				</button>
			</li>
		{/each}
	</ul>

	{#if showTable}
		<div class="table-wrap">
			<table class="numeric">
				<thead>
					<tr>
						<th scope="col">Time</th>
						{#each visible as s (s.id)}<th scope="col">{s.label}</th>{/each}
					</tr>
				</thead>
				<tbody>
					{#each tableRows as row (row.time)}
						<tr>
							<th scope="row">{formatInstant(row.time, span)}</th>
							{#each row.values as value, i (visible[i].id)}<td>{format(value)}</td>{/each}
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	{:else}
		<div class="plot" class:loading bind:clientWidth={width}>
			<!-- The plot is a focusable image whose values are read with the arrow keys; the table view is the full equivalent. -->
			<!-- svelte-ignore a11y_no_noninteractive_tabindex, a11y_no_noninteractive_element_interactions -->
			<svg
				{width}
				height={HEIGHT}
				role="img"
				aria-label="{title}. Use the left and right arrow keys to read values, or switch to the table view."
				tabindex="0"
				onpointermove={pointerMove}
				onpointerleave={() => (hoverTime = null)}
				onkeydown={keyDown}
				onblur={() => (hoverTime = null)}
			>
				{#each yTicks as tick (tick)}
					<line class="grid" x1={MARGIN.left} x2={width - MARGIN.right} y1={y(tick)} y2={y(tick)} />
					<text class="tick" x={MARGIN.left - 8} y={y(tick)} text-anchor="end" dominant-baseline="middle">{tick}</text>
				{/each}
				{#each xTicks as tick (tick)}
					<text class="tick" x={x(tick)} y={HEIGHT - 8} text-anchor="middle">{formatTick(tick, span)}</text>
				{/each}
				<line class="axis" x1={MARGIN.left} x2={width - MARGIN.right} y1={HEIGHT - MARGIN.bottom} y2={HEIGHT - MARGIN.bottom} />

				{#each visible as s (s.id)}
					<path d={path(s.points)} fill="none" stroke={s.color} stroke-width="2" stroke-linejoin="round" stroke-linecap="round" />
				{/each}

				{#if readout}
					<line class="crosshair" x1={x(readout.time)} x2={x(readout.time)} y1={MARGIN.top} y2={HEIGHT - MARGIN.bottom} />
					{#each readout.rows as row (row.series.id)}
						<!-- The surface-coloured ring keeps a marker legible where it sits on top of other lines. -->
						<circle cx={x(readout.time)} cy={y(row.value)} r="4" fill={row.series.color} stroke="var(--surface)" stroke-width="2" />
					{/each}
				{/if}
			</svg>

			{#if readout}
				<div class="tooltip" style:left="{tooltipLeft}px" role="status">
					<div class="when">{formatInstant(readout.time, span)}</div>
					{#each [...readout.rows].sort((a, b) => b.value - a.value) as row (row.series.id)}
						<div class="row">
							<span class="line-key" style:background={row.series.color}></span>
							<strong class="numeric">{format(row.value)}</strong>
							<span class="muted">{row.series.label}</span>
						</div>
					{/each}
				</div>
			{/if}

			{#if visible.length === 0 && !loading}
				<p class="empty muted">No history for this range yet.</p>
			{/if}
		</div>
	{/if}
</figure>

<style>
	.chart {
		margin: 0;
	}

	figcaption {
		display: flex;
		justify-content: space-between;
		align-items: center;
		gap: 12px;
	}

	.table-toggle {
		background: none;
		border: 1px solid var(--border);
		border-radius: 6px;
		padding: 2px 8px;
		font-size: 12px;
		color: var(--text-secondary);
		cursor: pointer;
	}

	.table-toggle:hover {
		background: var(--hover);
	}

	.legend {
		display: flex;
		flex-wrap: wrap;
		gap: 2px 6px;
		list-style: none;
		padding: 0;
		margin: 10px 0 4px -6px;
	}

	.legend button {
		display: flex;
		align-items: center;
		gap: 6px;
		background: none;
		border: 0;
		border-radius: 6px;
		padding: 3px 6px;
		cursor: pointer;
		font-size: 12px;
	}

	.legend button:hover {
		background: var(--hover);
	}

	.legend button.off {
		opacity: 0.4;
	}

	/* A short stroke, mirroring the mark: these are lines, so the key is a line. */
	.key {
		width: 14px;
		height: 3px;
		border-radius: 2px;
	}

	/* Text stays in ink; the coloured key beside it carries identity. */
	.name {
		color: var(--text-secondary);
	}

	.value {
		color: var(--text-primary);
		font-weight: 600;
	}

	.plot {
		position: relative;
		transition: opacity 120ms;
	}

	/* A refetch holds the previous render at reduced opacity: no skeleton, no layout jump. */
	.plot.loading {
		opacity: 0.55;
	}

	svg {
		display: block;
		touch-action: pan-y;
		outline: none;
	}

	svg:focus-visible {
		outline: 2px solid var(--series-1);
		outline-offset: 2px;
		border-radius: 4px;
	}

	.grid {
		stroke: var(--grid);
		stroke-width: 1;
	}

	.axis {
		stroke: var(--axis);
		stroke-width: 1;
	}

	.crosshair {
		stroke: var(--text-muted);
		stroke-width: 1;
	}

	.tick {
		fill: var(--text-muted);
		font-size: 11px;
		font-variant-numeric: tabular-nums;
	}

	.tooltip {
		position: absolute;
		top: 8px;
		min-width: 150px;
		padding: 8px 10px;
		background: var(--surface);
		border: 1px solid var(--border);
		border-radius: 8px;
		box-shadow: 0 4px 16px rgba(0, 0, 0, 0.18);
		font-size: 12px;
		pointer-events: none;
	}

	.when {
		color: var(--text-secondary);
		margin-bottom: 4px;
	}

	.row {
		display: flex;
		align-items: center;
		gap: 6px;
		white-space: nowrap;
	}

	.line-key {
		width: 10px;
		height: 2px;
		flex: none;
	}

	.empty {
		position: absolute;
		inset: 0;
		display: grid;
		place-items: center;
	}

	.table-wrap {
		max-height: 266px;
		overflow: auto;
		margin-top: 6px;
	}

	table {
		width: 100%;
		border-collapse: collapse;
		font-size: 12px;
	}

	th,
	td {
		padding: 4px 8px;
		text-align: right;
		border-bottom: 1px solid var(--grid);
		white-space: nowrap;
	}

	th:first-child {
		text-align: left;
	}

	thead th {
		position: sticky;
		top: 0;
		background: var(--surface);
		color: var(--text-secondary);
		font-weight: 600;
	}

	tbody th {
		font-weight: 400;
		color: var(--text-secondary);
	}
</style>
