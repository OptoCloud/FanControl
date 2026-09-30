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
	/**
	 * Drives only: the /dev/disk/by-path name of the port (bay) the drive is plugged into, e.g.
	 * "pci-0000:01:00.1-ata-3". The id says which disk; this says where it sits. Absent from
	 * daemons that predate ports.
	 */
	port?: string | null;
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
	port?: string | null;
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

// ---- The UPS, read from NUT's upsd (not from the daemon) ----

/**
 * One poll of a UPS's variables (`LIST VAR <ups>`). The named fields are the handful the
 * dashboard charts and alerts on; `variables` is everything upsd reported, verbatim.
 * Any of them is null when this UPS or driver doesn't provide it.
 */
export interface UpsReading {
	timestampUtc: string;
	/** The UPS's name on upsd, e.g. "apc". */
	name: string;
	model: string | null;
	/** ups.status split into its flags: OL, OB, LB, HB, RB, CHRG, DISCHRG, BYPASS, CAL, OFF, OVER, TRIM, BOOST, FSD. */
	status: string[];
	/** Percent. */
	batteryCharge: number | null;
	batteryRuntimeSeconds: number | null;
	/** Percent of the UPS's capacity. */
	load: number | null;
	/** Watts, derived from load and ups.realpower.nominal when the UPS doesn't report it directly. */
	realPower: number | null;
	inputVoltage: number | null;
	outputVoltage: number | null;
	batteryVoltage: number | null;
	variables: Record<string, string>;
}

export interface UpsState {
	/** False when NUT isn't configured (no NUT_HOST): the UPS section is hidden, not shown as broken. */
	enabled: boolean;
	/** The latest reading, or null while upsd can't be reached or has no fresh data for the UPS. */
	reading: UpsReading | null;
	/** Why there is no reading: a connection error, or upsd's own (DATA-STALE, DRIVER-NOT-CONNECTED, UNKNOWN-UPS). */
	error: string | null;
}

// ---- What the dashboard adds on top ----

/** A drive's last GOOD health result. The daemon only reports what its latest poll saw, and a sleeping drive isn't woken, so this is what survives those gaps. */
export interface DriveState {
	wwn: string;
	/** The bay it was in when it last answered. */
	port: string | null;
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
	| { type: 'drives'; drives: DriveState[] }
	| { type: 'ups'; ups: UpsState };

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
	/**
	 * Keyed by series id: a sensor id ("drive:<wwn>" follows a disk), a bay ("port:<by-path>"
	 * follows a location, whichever disk is in it), or "drives:max" / "memory:max" for the
	 * hottest of a group.
	 */
	temperatures: Record<string, SeriesPoints>;
	/** Keyed by fan id. */
	duties: Record<string, SeriesPoints>;
	rpms: Record<string, SeriesPoints>;
	/** The UPS: "charge" and "load" (%), "runtime" (minutes), "inputVoltage" (V). Empty without NUT. */
	ups: Partial<Record<UpsMetric, SeriesPoints>>;
}

export type UpsMetric = 'charge' | 'load' | 'runtime' | 'inputVoltage';
