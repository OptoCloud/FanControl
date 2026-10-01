<script lang="ts">
	import { relativeTime } from '$lib/chart/scale';
	import type { DriveState, HistoryResponse, SensorReading, SeriesPoints } from '$lib/types';
	import Sparkline from './Sparkline.svelte';
	import StatusBadge from './StatusBadge.svelte';

	interface Props {
		/** Live temperatures, from the daemon's snapshot. */
		sensors: SensorReading[];
		/** Last good SMART state per drive, which this app remembers across the drive's naps. */
		drives: DriveState[];
		/** The selected range's history, for each row's trend. */
		history: HistoryResponse | null;
		now: number;
	}

	let { sensors, drives, history, now }: Props = $props();

	// Two ways to read the same temperatures. By drive follows a disk wherever it is plugged
	// in (is this disk running warm?); by bay follows a spot in the case whichever disk sits
	// there (is this bay getting enough air?). They only differ once a disk has been moved.
	let view = $state<'drive' | 'bay'>('drive');

	// Thirteen drives are far past where colour can tell series apart, so this is a table,
	// with a small magnitude bar doing the at-a-glance comparison.
	const BAR_MIN = 20;
	const BAR_MAX = 60;

	const rows = $derived.by(() => {
		const health = new Map(drives.map((drive) => [drive.wwn, drive]));
		const temperatures = new Map(sensors.filter((s) => s.category === 'drive').map((s) => [s.id.replace(/^drive:/, ''), s]));

		return [...new Set([...temperatures.keys(), ...health.keys()])]
			.map((wwn) => {
				const sensor = temperatures.get(wwn);
				const port = sensor?.port ?? health.get(wwn)?.port ?? null;
				return { wwn, port, sensor, health: health.get(wwn), trend: series(`drive:${wwn}`) };
			})
			.sort((a, b) => (b.sensor?.celsiusOrNull ?? -1) - (a.sensor?.celsiusOrNull ?? -1) || a.wwn.localeCompare(b.wwn));
	});

	/** Every bay that has a drive now or had one in the selected range, in physical order. */
	const bays = $derived.by(() => {
		const health = new Map(drives.map((drive) => [drive.wwn, drive]));
		const occupants = new Map(sensors.filter((s) => s.category === 'drive' && s.port).map((s) => [s.port as string, s]));
		const recorded = Object.keys(history?.temperatures ?? {})
			.filter((id) => id.startsWith('port:'))
			.map((id) => id.slice('port:'.length));

		return [...new Set([...occupants.keys(), ...recorded])]
			.sort((a, b) => a.localeCompare(b, undefined, { numeric: true }))
			.map((port) => {
				const sensor = occupants.get(port);
				const wwn = sensor?.id.replace(/^drive:/, '');
				return { port, wwn, sensor, health: wwn ? health.get(wwn) : undefined, trend: series(`port:${port}`) };
			});
	});

	const series = (id: string): SeriesPoints => history?.temperatures[id] ?? [];

	/** "pci-0000:03:00.0-sas-phy2-lun-0" -> "sas-phy2", "pci-0000:01:00.1-ata-3" -> "ata-3": the controller is in the title. */
	const shortPort = (port: string | null | undefined) => port?.replace(/^pci-[0-9a-f:.]+-/i, '').replace(/-lun-0$/, '') ?? '';

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
	<div class="header">
		<h2 class="card-title">Drives</h2>
		<div class="segmented" role="group" aria-label="Group temperatures by">
			<button type="button" aria-pressed={view === 'drive'} onclick={() => (view = 'drive')}>By drive</button>
			<button type="button" aria-pressed={view === 'bay'} onclick={() => (view = 'bay')}>By bay</button>
		</div>
	</div>
	<div class="scroll">
		{#if view === 'drive'}
			<table>
				<thead>
					<tr>
						<th scope="col">Drive</th>
						<th scope="col">Bay</th>
						<th scope="col">Temperature</th>
						<th scope="col">Trend</th>
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
						<tr>
							<!-- The WWN is the drive's identity: unlike sdX it survives reboots and port changes. -->
							<th scope="row" title={row.wwn}>
								<span class="wwn numeric">{row.wwn.slice(-8)}</span>
								<span class="muted">{device(row.health?.sourcePath)}</span>
							</th>
							<td class="numeric" title={row.port ?? ''}>{shortPort(row.port)}</td>
							<td>{@render temperature(row.sensor?.celsiusOrNull ?? null)}</td>
							<td><Sparkline points={row.trend} /></td>
							<td><StatusBadge level={s.level} label={s.label} /></td>
							<td class="right numeric">{count(row.health?.reallocatedSectorCount)}</td>
							<td class="right numeric">{count(row.health?.pendingSectorCount)}</td>
							<td class="right numeric">{years(row.health?.powerOnHours)}</td>
							<td class="right muted numeric">{row.health ? relativeTime(row.health.asOf, now) : ''}</td>
						</tr>
					{:else}
						<tr><td colspan="9" class="muted">No drives reported yet.</td></tr>
					{/each}
				</tbody>
			</table>
		{:else}
			<table>
				<thead>
					<tr>
						<th scope="col">Bay</th>
						<th scope="col">Drive in it</th>
						<th scope="col">Temperature</th>
						<th scope="col">Trend</th>
						<th scope="col">SMART</th>
					</tr>
				</thead>
				<tbody>
					{#each bays as bay (bay.port)}
						{@const s = smart(bay.health)}
						<tr>
							<!-- The port is the bay's identity: it stays with the location whatever disk is in it. -->
							<th scope="row" class="numeric" title={bay.port}>{shortPort(bay.port)}</th>
							<td title={bay.wwn ?? ''}>
								{#if bay.wwn}<span class="wwn numeric">{bay.wwn.slice(-8)}</span>{:else}<span class="muted">empty now</span>{/if}
							</td>
							<td>{@render temperature(bay.sensor?.celsiusOrNull ?? null)}</td>
							<td><Sparkline points={bay.trend} /></td>
							<td
								>{#if bay.wwn}<StatusBadge level={s.level} label={s.label} />{/if}</td
							>
						</tr>
					{:else}
						<tr><td colspan="5" class="muted">No bays reported yet: that needs a daemon that reports each drive's port.</td></tr>
					{/each}
				</tbody>
			</table>
		{/if}
	</div>
</section>

{#snippet temperature(celsius: number | null)}
	{#if celsius === null}
		<span class="muted">asleep or unreadable</span>
	{:else}
		<div class="temperature">
			<div class="bar-slot"><div class="bar" style:width="{barWidth(celsius)}%"></div></div>
			<span class="numeric">{celsius.toFixed(0)}°C</span>
		</div>
	{/if}
{/snippet}

<style>
	.header {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		justify-content: space-between;
		gap: 8px 16px;
	}

	.segmented {
		display: inline-flex;
		border: 1px solid var(--grid);
		border-radius: 6px;
		overflow: hidden;
	}

	.segmented button {
		font: inherit;
		font-size: 12px;
		padding: 4px 10px;
		border: 0;
		background: transparent;
		color: var(--text-secondary);
		cursor: pointer;
	}

	.segmented button[aria-pressed='true'] {
		background: var(--grid);
		color: inherit;
		font-weight: 600;
	}

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
