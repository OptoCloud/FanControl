<script lang="ts">
	import { relativeTime } from '$lib/chart/scale';
	import type { DriveState, SensorReading } from '$lib/types';
	import StatusBadge from './StatusBadge.svelte';

	interface Props {
		/** Live temperatures, from the daemon's snapshot. */
		sensors: SensorReading[];
		/** Last good SMART state per drive, which this app remembers across the drive's naps. */
		drives: DriveState[];
		now: number;
	}

	let { sensors, drives, now }: Props = $props();

	// Thirteen drives are far past where colour can tell series apart, so this is a table,
	// with a small magnitude bar doing the at-a-glance comparison.
	const BAR_MIN = 20;
	const BAR_MAX = 60;

	const rows = $derived.by(() => {
		const health = new Map(drives.map((drive) => [drive.wwn, drive]));
		const temperatures = new Map(sensors.filter((s) => s.category === 'drive').map((s) => [s.id.replace(/^drive:/, ''), s]));

		return [...new Set([...temperatures.keys(), ...health.keys()])]
			.map((wwn) => ({ wwn, sensor: temperatures.get(wwn), health: health.get(wwn) }))
			.sort((a, b) => (b.sensor?.celsiusOrNull ?? -1) - (a.sensor?.celsiusOrNull ?? -1) || a.wwn.localeCompare(b.wwn));
	});

	const barWidth = (celsius: number) => Math.min(100, Math.max(3, ((celsius - BAR_MIN) / (BAR_MAX - BAR_MIN)) * 100));

	function smart(drive: DriveState | undefined): { level: 'good' | 'warning' | 'critical' | 'unknown'; label: string } {
		if (!drive || drive.passed === null) return { level: 'unknown', label: 'No data yet' };
		if (!drive.passed) return { level: 'critical', label: 'FAILED' };
		if ((drive.pendingSectorCount ?? 0) > 0) return { level: 'warning', label: 'Pending sectors' };
		if ((drive.reallocatedSectorCount ?? 0) > 0) return { level: 'warning', label: 'Reallocated sectors' };
		return { level: 'good', label: 'Passed' };
	}

	const count = (value: number | null | undefined) => (value == null ? 'n/a' : value.toLocaleString());
	const years = (hours: number | null | undefined) => (hours == null ? 'n/a' : `${(hours / 8766).toFixed(1)} y`);
	const device = (path: string | undefined) => path?.replace('/dev/', '') ?? '';
</script>

<section class="card">
	<h2 class="card-title">Drives</h2>
	<div class="scroll">
		<table>
			<thead>
				<tr>
					<th scope="col">Drive</th>
					<th scope="col">Temperature</th>
					<th scope="col">SMART</th>
					<th scope="col" class="right">Reallocated</th>
					<th scope="col" class="right">Pending</th>
					<th scope="col" class="right">Powered on</th>
					<th scope="col" class="right">Checked</th>
				</tr>
			</thead>
			<tbody>
				{#each rows as row (row.wwn)}
					{@const s = smart(row.health)}
					{@const celsius = row.sensor?.celsiusOrNull ?? null}
					<tr>
						<!-- The WWN is the drive's identity: unlike sdX it survives reboots and port changes. -->
						<th scope="row" title={row.wwn}>
							<span class="wwn numeric">{row.wwn.slice(-8)}</span>
							<span class="muted">{device(row.health?.sourcePath)}</span>
						</th>
						<td>
							{#if celsius === null}
								<span class="muted">asleep or unreadable</span>
							{:else}
								<div class="temperature">
									<div class="bar-slot"><div class="bar" style:width="{barWidth(celsius)}%"></div></div>
									<span class="numeric">{celsius.toFixed(0)}°C</span>
								</div>
							{/if}
						</td>
						<td><StatusBadge level={s.level} label={s.label} /></td>
						<td class="right numeric">{count(row.health?.reallocatedSectorCount)}</td>
						<td class="right numeric">{count(row.health?.pendingSectorCount)}</td>
						<td class="right numeric">{years(row.health?.powerOnHours)}</td>
						<td class="right muted numeric">{row.health ? relativeTime(row.health.asOf, now) : ''}</td>
					</tr>
				{:else}
					<tr><td colspan="7" class="muted">No drives reported yet.</td></tr>
				{/each}
			</tbody>
		</table>
	</div>
</section>

<style>
	.scroll {
		overflow-x: auto;
		margin-top: 8px;
	}

	table {
		width: 100%;
		border-collapse: collapse;
	}

	th,
	td {
		padding: 6px 12px 6px 0;
		border-bottom: 1px solid var(--grid);
		text-align: left;
		font-weight: 400;
		white-space: nowrap;
	}

	thead th {
		font-size: 12px;
		color: var(--text-secondary);
		font-weight: 600;
	}

	tbody tr:last-child th,
	tbody tr:last-child td {
		border-bottom: 0;
	}

	.right {
		text-align: right;
		padding-right: 0;
		padding-left: 12px;
	}

	.wwn {
		margin-right: 6px;
	}

	.temperature {
		display: flex;
		align-items: center;
		gap: 8px;
		min-width: 150px;
	}

	.bar-slot {
		flex: 1;
	}

	/* Thin, one hue, square at the baseline and rounded at the data end. */
	.bar {
		height: 8px;
		border-radius: 0 4px 4px 0;
		background: var(--series-1);
	}
</style>
