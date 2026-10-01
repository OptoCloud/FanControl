<script lang="ts">
	import { onMount } from 'svelte';
	import { humanize, relativeTime } from '$lib/chart/scale';
	import DriveTable from '$lib/components/DriveTable.svelte';
	import EventLog from '$lib/components/EventLog.svelte';
	import FanList from '$lib/components/FanList.svelte';
	import LineChart from '$lib/components/LineChart.svelte';
	import StatTile from '$lib/components/StatTile.svelte';
	import StatusBadge from '$lib/components/StatusBadge.svelte';
	import UpsPanel from '$lib/components/UpsPanel.svelte';
	import type {
		ChartSeries,
		DriveState,
		EventRecord,
		HistoryResponse,
		LiveMessage,
		RangeKey,
		SensorReading,
		SeriesPoints,
		Snapshot,
		UpsState
	} from '$lib/types';
	import { summarizeUps } from '$lib/ups';

	// The page is a static build with no server of its own, so it starts empty: the live stream's
	// first messages are vigil-core's full current state, and the event log is fetched once.
	let snapshot = $state<Snapshot | null>(null);
	let daemonConnected = $state(false);
	let drives = $state<DriveState[]>([]);
	let events = $state<EventRecord[]>([]);
	// Disabled, so hidden until vigil-core sends the real one: none of these values is ever shown.
	let ups = $state<UpsState>({
		enabled: false,
		name: '',
		reading: null,
		error: null,
		limits: { hostShutdownSeconds: null, batteryTemperatureWarn: 0 }
	});

	// The stream IS the connection to vigil-core: there is no relay in between any more, so
	// whether EventSource is open is whether vigil-core can be reached.
	let stream = $state<'connecting' | 'open' | 'lost'>('connecting');
	let now = $state(Date.now());

	let range = $state<RangeKey>('1h');
	let history = $state<HistoryResponse | null>(null);
	let historyError = $state('');
	let loading = $state(false);
	let theme = $state<'auto' | 'light' | 'dark'>('auto');

	const RANGES: { key: RangeKey; label: string }[] = [
		{ key: '1h', label: 'Last hour' },
		{ key: '6h', label: '6 hours' },
		{ key: '24h', label: '24 hours' },
		{ key: '7d', label: '7 days' },
		{ key: '30d', label: '30 days' },
		{ key: '1y', label: '1 year' }
	];

	// Colour follows the entity, in a fixed order: the CPU line is the same blue in every
	// range and every session. Thirteen drives and two DIMMs would blow through what colour
	// can distinguish, so each group is charted as one line: its hottest member.
	const TEMPERATURE_SERIES = [
		{ id: 'cpu', label: 'CPU', color: 'var(--series-1)' },
		{ id: 'gpu', label: 'GPU', color: 'var(--series-2)' },
		{ id: 'hba', label: 'HBA', color: 'var(--series-3)' },
		{ id: 'drives:max', label: 'Hottest drive', color: 'var(--series-4)' },
		{ id: 'board', label: 'Board', color: 'var(--series-5)' },
		{ id: 'memory:max', label: 'Hottest DIMM', color: 'var(--series-6)' }
	];
	const MAX_SERIES = 6;

	onMount(() => {
		const stored = localStorage.getItem('theme');
		if (stored === 'light' || stored === 'dark') theme = stored;

		// EventSource reconnects on its own; onerror/onopen just keep the header honest about it.
		// vigil-core's keepalive is a named `keepalive` event, which onmessage never receives.
		const source = new EventSource('/api/live');
		source.onopen = () => (stream = 'open');
		source.onerror = () => (stream = 'lost');
		source.onmessage = (message) => {
			const live = JSON.parse(message.data) as LiveMessage;
			if (live.type === 'snapshot') snapshot = live.snapshot;
			else if (live.type === 'daemon') daemonConnected = live.connected;
			else if (live.type === 'drives') drives = live.drives;
			else if (live.type === 'event') events = [live.event, ...events].slice(0, 100);
			else if (live.type === 'ups') ups = live.ups;
		};

		void loadEvents();

		const clock = setInterval(() => (now = Date.now()), 1000);
		const refresh = setInterval(() => void loadHistory(range, false), 30_000);
		return () => {
			source.close();
			clearInterval(clock);
			clearInterval(refresh);
		};
	});

	$effect(() => {
		void loadHistory(range, true);
	});

	/**
	 * The newest events, for what the stream cannot replay: it carries only events raised after
	 * it connects. Merged by id, so one raised between the two requests is not shown twice.
	 * vigil-core answers [] when the database is down, which leaves the live ones in place.
	 */
	async function loadEvents() {
		try {
			const response = await fetch('/api/events');
			if (!response.ok) return;
			const logged = (await response.json()) as EventRecord[];
			const live = new Set(events.map((event) => event.id));
			events = [...events, ...logged.filter((event) => !live.has(event.id))].slice(0, 100);
		} catch {
			// The header already says when vigil-core cannot be reached.
		}
	}

	async function loadHistory(requested: RangeKey, showLoading: boolean) {
		if (showLoading) loading = true;
		try {
			const response = await fetch(`/api/history?range=${requested}`);
			// vigil-core explains a failure in plain text ("History is unavailable: ...").
			if (!response.ok) throw new Error((await response.text().catch(() => '')).trim() || `HTTP ${response.status}`);
			const result = (await response.json()) as HistoryResponse;
			// A slow response for a range the user has since left must not overwrite the current one.
			if (requested === range) {
				history = result;
				historyError = '';
			}
		} catch (error) {
			if (requested === range) historyError = error instanceof Error ? error.message : String(error);
		} finally {
			if (requested === range) loading = false;
		}
	}

	function setTheme(next: 'auto' | 'light' | 'dark') {
		theme = next;
		if (next === 'auto') {
			delete document.documentElement.dataset.theme;
			localStorage.removeItem('theme');
		} else {
			document.documentElement.dataset.theme = next;
			localStorage.setItem('theme', next);
		}
	}

	const sensors = $derived(snapshot?.sensors ?? []);
	const sensor = (id: string) => sensors.find((s) => s.id === id);
	const hottest = (category: SensorReading['category']) =>
		sensors
			.filter((s) => s.category === category && s.celsiusOrNull !== null)
			.sort((a, b) => (b.celsiusOrNull as number) - (a.celsiusOrNull as number))[0];

	const hottestDrive = $derived(hottest('drive'));
	const hottestDimm = $derived(hottest('memory'));
	const snapshotTime = $derived(snapshot ? new Date(snapshot.timestampUtc).getTime() : 0);
	const snapshotAge = $derived(snapshot ? now - snapshotTime : Infinity);

	const liveTemperature = $derived<Record<string, number | null | undefined>>({
		cpu: sensor('cpu')?.celsiusOrNull,
		gpu: sensor('gpu')?.celsiusOrNull,
		hba: sensor('hba')?.celsiusOrNull,
		board: sensor('board')?.celsiusOrNull,
		'drives:max': hottestDrive?.celsiusOrNull,
		'memory:max': hottestDimm?.celsiusOrNull
	});

	// History is bucketed and up to 30s old; appending the live reading makes every line reach "now".
	function withLivePoint(points: SeriesPoints | undefined, value: number | null | undefined): SeriesPoints {
		const base = points ?? [];
		if (value == null || !snapshot || snapshotAge > 60_000) return base;
		const last = base.at(-1);
		return last && last[0] >= snapshotTime ? base : [...base, [snapshotTime, value]];
	}

	const chartTo = $derived(Math.max(history?.to ?? 0, snapshotAge < 60_000 ? snapshotTime : 0));
	const chartFrom = $derived(history ? chartTo - (history.to - history.from) : 0);

	const temperatureSeries = $derived<ChartSeries[]>(
		TEMPERATURE_SERIES.map((s) => ({ ...s, points: withLivePoint(history?.temperatures[s.id], liveTemperature[s.id]) }))
	);

	const fanIds = $derived([...new Set([...Object.keys(history?.duties ?? {}), ...(snapshot?.fans.map((fan) => fan.id) ?? [])])].sort());
	const dutySeries = $derived<ChartSeries[]>(
		fanIds.slice(0, MAX_SERIES).map((id, index) => ({
			id,
			label: humanize(id),
			color: `var(--series-${index + 1})`,
			points: withLivePoint(history?.duties[id], snapshot?.fans.find((fan) => fan.id === id)?.dutyPercent)
		}))
	);

	// Charge and load share one 0-100 axis. Runtime (minutes) and mains voltage are other units,
	// so they get their own chart or the panel, never a second y-scale.
	const upsReading = $derived(ups.reading);
	const upsTime = $derived(upsReading ? new Date(upsReading.timestampUtc).getTime() : 0);
	function withLiveUpsPoint(points: SeriesPoints | undefined, value: number | null | undefined): SeriesPoints {
		const base = points ?? [];
		if (value == null || !upsReading || now - upsTime > 60_000) return base;
		const last = base.at(-1);
		return last && last[0] >= upsTime ? base : [...base, [upsTime, value]];
	}
	const upsPercentSeries = $derived<ChartSeries[]>([
		{
			id: 'charge',
			label: 'Battery charge',
			color: 'var(--series-3)',
			points: withLiveUpsPoint(history?.ups.charge, upsReading?.batteryCharge)
		},
		{ id: 'load', label: 'Load', color: 'var(--series-2)', points: withLiveUpsPoint(history?.ups.load, upsReading?.load) }
	]);
	const mainsSeries = $derived<ChartSeries[]>([
		{
			id: 'inputVoltage',
			label: 'Mains in',
			color: 'var(--series-1)',
			points: withLiveUpsPoint(history?.ups.inputVoltage, upsReading?.inputVoltage)
		}
	]);
	const upsSummary = $derived(summarizeUps(ups));

	const tilePoints = (id: string) => temperatureSeries.find((s) => s.id === id)?.points ?? [];

	const status = $derived.by((): { level: 'good' | 'warning' | 'critical'; label: string; detail: string } => {
		if (stream === 'connecting') return { level: 'warning', label: 'Connecting', detail: 'Waiting for the live stream from vigil-core.' };
		if (stream === 'lost')
			return {
				level: 'critical',
				label: 'vigil-core unreachable',
				detail: 'This page cannot reach vigil-core, so nothing shown is live. It reconnects on its own.'
			};
		if (!daemonConnected)
			return {
				level: 'critical',
				label: 'Daemon unreachable',
				detail: 'vigil-core cannot reach vigild. If it is not running, the fans are on BIOS control.'
			};
		if (!snapshot) return { level: 'warning', label: 'Waiting for data', detail: 'Connected, but no snapshot has arrived yet.' };
		if (snapshotAge > 15_000)
			return { level: 'warning', label: 'Stale', detail: `The last snapshot is ${relativeTime(snapshot.timestampUtc, now)} old.` };
		if (!snapshot.controlLoopHealthy)
			return {
				level: 'critical',
				label: 'Control loop unhealthy',
				detail: 'A fan channel could not be driven, or the loop has stopped polling.'
			};
		return { level: 'good', label: 'Live', detail: '' };
	});
</script>

<main>
	<header>
		<div>
			<h1>vigil</h1>
			<p class="muted">
				{#if snapshot}
					{sensors.length} sensors, {snapshot.fans.length} fans, updated {relativeTime(snapshot.timestampUtc, now)}
				{:else}
					No data yet
				{/if}
			</p>
		</div>
		<div class="header-right">
			<div class="status" title={status.detail}><StatusBadge level={status.level} label={status.label} /></div>
			<div class="segmented" role="group" aria-label="Colour theme">
				{#each ['auto', 'light', 'dark'] as const as option (option)}
					<button type="button" aria-pressed={theme === option} onclick={() => setTheme(option)}>{humanize(option)}</button>
				{/each}
			</div>
		</div>
	</header>

	{#if status.detail}
		<p class="banner {status.level}" role="status">{status.detail}</p>
	{/if}
	{#if ups.enabled && upsSummary.banner}
		<p class="banner {upsSummary.level}" role="alert">{upsSummary.banner}</p>
	{/if}

	<!-- One filter row, above everything it scopes: the tile trends and both charts all follow this range. -->
	<div class="filters">
		<div class="segmented" role="group" aria-label="Time range">
			{#each RANGES as option (option.key)}
				<button type="button" aria-pressed={range === option.key} onclick={() => (range = option.key)}>{option.label}</button>
			{/each}
		</div>
		{#if historyError}
			<span class="muted" role="status">History unavailable: {historyError}</span>
		{/if}
	</div>

	<section class="tiles" aria-label="Temperatures">
		<StatTile label="CPU" celsius={sensor('cpu')?.celsiusOrNull ?? null} points={tilePoints('cpu')} />
		<StatTile label="GPU" celsius={sensor('gpu')?.celsiusOrNull ?? null} points={tilePoints('gpu')} />
		<StatTile label="HBA" celsius={sensor('hba')?.celsiusOrNull ?? null} points={tilePoints('hba')} />
		<StatTile
			label="Hottest drive"
			celsius={hottestDrive?.celsiusOrNull ?? null}
			points={tilePoints('drives:max')}
			detail={hottestDrive ? `drive ${hottestDrive.id.slice(-8)}` : undefined}
		/>
		<StatTile label="Board" celsius={sensor('board')?.celsiusOrNull ?? null} points={tilePoints('board')} />
		<StatTile label="Hottest DIMM" celsius={hottestDimm?.celsiusOrNull ?? null} points={tilePoints('memory:max')} />
	</section>

	<div class="charts">
		<LineChart title="Temperatures" series={temperatureSeries} from={chartFrom} to={chartTo} unit="°C" {loading} />
		<!-- Duty shares one fixed 0-100 axis. RPM is deliberately not overlaid on a second axis: a 2,700 rpm
		     HBA fan next to 600 rpm case fans would say nothing, and two y-scales invent correlations. -->
		<LineChart title="Fan duty" series={dutySeries} from={chartFrom} to={chartTo} unit="%" yDomain={[0, 100]} {loading} />
	</div>
	{#if fanIds.length > MAX_SERIES}
		<p class="muted note">
			{fanIds.length - MAX_SERIES} more fan(s) are not charted: six is as many lines as colour can keep apart. All of them are listed below.
		</p>
	{/if}

	{#if ups.enabled}
		<section class="power" aria-label="Power">
			<UpsPanel {ups} inputVoltage={history?.ups.inputVoltage ?? []} {now} />
			<LineChart
				title="UPS battery and load"
				series={upsPercentSeries}
				from={chartFrom}
				to={chartTo}
				unit="%"
				yDomain={[0, 100]}
				{loading}
			/>
			<LineChart title="Mains voltage" series={mainsSeries} from={chartFrom} to={chartTo} unit=" V" {loading} />
		</section>
	{/if}

	<div class="lower">
		<FanList fans={snapshot?.fans ?? []} />
		<EventLog {events} {now} />
	</div>

	<DriveTable {sensors} {drives} {history} {now} />
</main>

<style>
	main {
		max-width: 1280px;
		margin: 0 auto;
		padding: 20px 16px 48px;
		display: flex;
		flex-direction: column;
		gap: 14px;
	}

	header {
		display: flex;
		justify-content: space-between;
		align-items: center;
		gap: 12px;
		flex-wrap: wrap;
	}

	h1 {
		font-size: 20px;
		font-weight: 650;
		letter-spacing: -0.01em;
	}

	.header-right {
		display: flex;
		align-items: center;
		gap: 14px;
		flex-wrap: wrap;
	}

	.status {
		font-weight: 600;
	}

	.banner {
		padding: 10px 14px;
		border-radius: 8px;
		border: 1px solid var(--border);
		border-left: 4px solid var(--status-warning);
		background: var(--surface);
	}

	.banner.critical {
		border-left-color: var(--status-critical);
	}

	.filters {
		display: flex;
		align-items: center;
		gap: 12px;
		flex-wrap: wrap;
	}

	.segmented {
		display: inline-flex;
		border: 1px solid var(--border);
		border-radius: 8px;
		background: var(--surface);
		overflow: hidden;
	}

	.segmented button {
		background: none;
		border: 0;
		padding: 5px 11px;
		font-size: 13px;
		color: var(--text-secondary);
		cursor: pointer;
	}

	.segmented button + button {
		border-left: 1px solid var(--border);
	}

	.segmented button:hover {
		background: var(--hover);
	}

	.segmented button[aria-pressed='true'] {
		background: var(--hover);
		color: var(--text-primary);
		font-weight: 600;
	}

	.tiles {
		display: grid;
		grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
		gap: 12px;
	}

	.charts,
	.lower {
		display: grid;
		grid-template-columns: repeat(auto-fit, minmax(min(100%, 440px), 1fr));
		gap: 12px;
	}

	.power {
		display: grid;
		grid-template-columns: repeat(auto-fit, minmax(min(100%, 340px), 1fr));
		gap: 12px;
		align-items: start;
	}

	.note {
		font-size: 12px;
		margin-top: -6px;
	}
</style>
