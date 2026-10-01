<script lang="ts">
	import { relativeTime } from '$lib/chart/scale';
	import type { SeriesPoints, UpsState } from '$lib/types';
	import { formatRuntime, summarizeUps } from '$lib/ups';
	import StatusBadge from './StatusBadge.svelte';

	interface Props {
		ups: UpsState;
		/** Mains voltage history for the selected range, summarised as its lowest and highest. */
		inputVoltage: SeriesPoints;
		now: number;
	}

	let { ups, inputVoltage, now }: Props = $props();

	const reading = $derived(ups.reading);
	const summary = $derived(summarizeUps(ups));

	const voltageRange = $derived.by(() => {
		if (inputVoltage.length === 0) return null;
		const values = inputVoltage.map((point) => point[1]);
		return { min: Math.min(...values), max: Math.max(...values) };
	});

	const variables = $derived(reading ? Object.entries(reading.variables).sort(([a], [b]) => a.localeCompare(b)) : []);
	const volts = (value: number | null) => (value === null ? 'n/a' : `${value.toFixed(value >= 100 ? 0 : 1)} V`);
</script>

<section class="card">
	<div class="head">
		<div>
			<h2 class="card-title">UPS</h2>
			<p class="muted model">{reading?.model ?? ups.error ?? 'Waiting for NUT'}</p>
		</div>
		<StatusBadge level={summary.level} label={summary.label} />
	</div>

	{#if reading}
		<table>
			<tbody>
				<tr>
					<th scope="row">Battery</th>
					<td class="bar">
						{#if reading.batteryCharge !== null}
							<div
								class="meter"
								role="meter"
								aria-label="Battery charge"
								aria-valuemin="0"
								aria-valuemax="100"
								aria-valuenow={reading.batteryCharge}
							>
								<div class="fill" style:width="{reading.batteryCharge}%"></div>
							</div>
						{/if}
					</td>
					<td class="numeric value">{reading.batteryCharge === null ? 'n/a' : `${Math.round(reading.batteryCharge)}%`}</td>
				</tr>
				<tr>
					<th scope="row">Load</th>
					<td class="bar">
						{#if reading.load !== null}
							<div class="meter" role="meter" aria-label="UPS load" aria-valuemin="0" aria-valuemax="100" aria-valuenow={reading.load}>
								<div class="fill" style:width="{Math.min(100, reading.load)}%"></div>
							</div>
						{/if}
					</td>
					<td class="numeric value">
						{reading.load === null ? 'n/a' : `${Math.round(reading.load)}%`}
						{#if reading.realPower !== null}<span class="muted">{reading.realPower} W</span>{/if}
					</td>
				</tr>
				<tr>
					<th scope="row">Runtime</th>
					<td colspan="2" class="numeric value"
						>{reading.batteryRuntimeSeconds === null ? 'n/a' : formatRuntime(reading.batteryRuntimeSeconds)}</td
					>
				</tr>
				<tr>
					<th scope="row">Mains in</th>
					<td colspan="2" class="numeric value">
						{volts(reading.inputVoltage)}
						{#if voltageRange}<span class="muted">{voltageRange.min.toFixed(0)} to {voltageRange.max.toFixed(0)} V in range</span>{/if}
					</td>
				</tr>
				<tr>
					<th scope="row">Output</th>
					<td colspan="2" class="numeric value">{volts(reading.outputVoltage)}</td>
				</tr>
				<tr>
					<th scope="row">Battery voltage</th>
					<td colspan="2" class="numeric value">{volts(reading.batteryVoltage)}</td>
				</tr>
			</tbody>
		</table>

		<details>
			<summary class="muted">All {variables.length} variables from upsd, read {relativeTime(reading.timestampUtc, now)}</summary>
			<table class="variables numeric">
				<tbody>
					{#each variables as [name, value] (name)}
						<tr><th scope="row">{name}</th><td>{value}</td></tr>
					{/each}
				</tbody>
			</table>
		</details>
	{:else}
		<p class="muted empty">
			{ups.error ? `No reading: ${ups.error}.` : 'Connecting to upsd.'} This only affects the dashboard; orion's own upsmon still guards the shutdown.
		</p>
	{/if}
</section>

<style>
	.head {
		display: flex;
		justify-content: space-between;
		align-items: flex-start;
		gap: 12px;
	}

	.head :global(.badge) {
		font-size: 13px;
		font-weight: 600;
	}

	.model {
		font-size: 12px;
	}

	table {
		width: 100%;
		border-collapse: collapse;
		margin-top: 8px;
	}

	th,
	td {
		padding: 7px 0;
		border-bottom: 1px solid var(--grid);
		text-align: left;
		font-weight: 400;
		white-space: nowrap;
	}

	tr:last-child th,
	tr:last-child td {
		border-bottom: 0;
	}

	th {
		padding-right: 12px;
		color: var(--text-secondary);
	}

	.bar {
		width: 100%;
	}

	.meter {
		min-width: 60px;
		height: 8px;
		border-radius: 4px;
		background: var(--meter-track);
		overflow: hidden;
	}

	.fill {
		height: 100%;
		border-radius: 4px;
		background: var(--meter-fill);
		transition: width 400ms ease;
	}

	.value {
		font-weight: 600;
		text-align: right;
		padding-left: 12px;
	}

	.value .muted {
		font-weight: 400;
		margin-left: 6px;
	}

	details {
		margin-top: 8px;
		font-size: 12px;
	}

	summary {
		cursor: pointer;
	}

	.variables {
		font-size: 12px;
	}

	.variables th,
	.variables td {
		padding: 3px 0;
		white-space: normal;
		word-break: break-word;
	}

	.variables td {
		text-align: right;
	}

	.empty {
		margin-top: 12px;
	}
</style>
