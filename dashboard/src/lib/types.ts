// The daemon's snapshot contract (see the API section of the repo README). Field names are
// the daemon's; treat a change here as a change to that contract.

export type SensorCategory = 'cpu' | 'boardAmbient' | 'drive' | 'gpu' | 'memory' | 'hba';

export interface SensorReading {
	id: string;
	category: SensorCategory;
	label: string;
	celsiusOrNull: number | null;
	sourcePath: string;
	isAvailable: boolean;
}

export type PwmMode = 'disabled' | 'manual' | 'thermalCruise' | 'speedCruise' | 'smartFanIII' | 'smartFanIV';

export interface FanStatus {
	id: string;
	dutyPercent: number;
	rpm: number | null;
	mode: PwmMode | null;
	stalled: boolean;
}

export interface DriveHealth {
	deviceName: string;
	passed: boolean | null;
	reallocatedSectorCount: number | null;
	pendingSectorCount: number | null;
	powerOnHours: number | null;
	sourcePath: string;
	isAvailable: boolean;
	asOf: string;
}

export interface Snapshot {
	timestampUtc: string;
	sensors: SensorReading[];
	fans: FanStatus[];
	driveHealth: DriveHealth[];
	controlLoopHealthy: boolean;
}

// ---- What the dashboard adds on top ----

/** A drive's last GOOD health result. The daemon only reports what its latest poll saw, and a sleeping drive isn't woken, so this is what survives those gaps. */
export interface DriveState {
	wwn: string;
	passed: boolean | null;
	reallocatedSectorCount: number | null;
	pendingSectorCount: number | null;
	powerOnHours: number | null;
	sourcePath: string;
	/** When the drive last actually answered. */
	asOf: string;
}

export type Severity = 'info' | 'warning' | 'critical';

export interface EventRecord {
	id: number;
	ts: string;
	severity: Severity;
	/** Stable key for the condition, e.g. "fan-stalled:drive-cage". */
	kind: string;
	message: string;
}

/** What a browser receives over /api/live. */
export type LiveMessage =
	| { type: 'snapshot'; snapshot: Snapshot }
	| { type: 'daemon'; connected: boolean }
	| { type: 'event'; event: EventRecord }
	| { type: 'drives'; drives: DriveState[] };

export type RangeKey = '1h' | '6h' | '24h' | '7d' | '30d';

/** One series of [epochMillis, value] points. */
export type SeriesPoints = [number, number][];

export interface ChartSeries {
	id: string;
	label: string;
	/** A CSS colour, normally a var(--series-N) token. Colour follows the entity, never its position. */
	color: string;
	points: SeriesPoints;
}

export interface HistoryResponse {
	range: RangeKey;
	from: number;
	to: number;
	bucketSeconds: number;
	/** Keyed by series id: a sensor id, or "drives:max" / "memory:max" for the hottest of a group. */
	temperatures: Record<string, SeriesPoints>;
	/** Keyed by fan id. */
	duties: Record<string, SeriesPoints>;
	rpms: Record<string, SeriesPoints>;
}
