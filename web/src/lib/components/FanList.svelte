<script lang="ts">
	import { humanize } from '$lib/chart/scale';
	import type { FanStatus } from '$lib/types';
	import StatusBadge from './StatusBadge.svelte';

	let { fans }: { fans: FanStatus[] } = $props();

	const sorted = $derived([...fans].sort((a, b) => a.id.localeCompare(b.id)));

	function state(fan: FanStatus): { level: 'good' | 'warning' | 'critical'; label: string } {
		if (fan.stalled) return { level: 'critical', label: 'Stalled' };
		if (fan.mode !== 'manual') return { level: 'warning', label: fan.mode === null ? 'Mode unreadable' : `Not controlled (${fan.mode})` };
		return { level: 'good', label: 'Controlled' };
	}
</script>

<section class="card">
	<h2 class="card-title">Fans</h2>
	<table>
		<thead class="visually-hidden">
			<tr><th scope="col">Fan</th><th scope="col">Duty</th><th scope="col">Speed</th><th scope="col">State</th></tr>
		</thead>
		<tbody>
			{#each sorted as fan (fan.id)}
				{@const s = state(fan)}
				<tr>
					<th scope="row">{humanize(fan.id)}</th>
					<td class="duty">
						<!-- A single ratio against a limit is a meter. The track is a lighter step of the fill's own ramp. -->
						<div class="meter" role="meter" aria-label="{humanize(fan.id)} duty" aria-valuemin="0" aria-valuemax="100" aria-valuenow={fan.dutyPercent}>
							<div class="fill" style:width="{fan.dutyPercent}%"></div>
						</div>
						<span class="numeric percent">{fan.dutyPercent}%</span>
					</td>
					<td class="numeric rpm">{fan.rpm === null ? 'n/a' : fan.rpm.toLocaleString()} <span class="muted">rpm</span></td>
					<td class="state"><StatusBadge level={s.level} label={s.label} /></td>
				</tr>
			{:else}
				<tr><td class="muted">Waiting for the daemon.</td></tr>
			{/each}
		</tbody>
	</table>
</section>

<style>
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
	}

	.duty {
		width: 100%;
		display: flex;
		align-items: center;
		gap: 8px;
	}

	.meter {
		flex: 1;
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

	.percent {
		width: 38px;
		text-align: right;
		font-weight: 600;
	}

	.rpm {
		text-align: right;
		padding: 0 16px;
	}

	.state {
		font-size: 12px;
	}
</style>
