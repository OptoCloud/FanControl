// A stand-in for the fancontrol daemon, for developing the dashboard without the hardware
// (and on Windows, where the real daemon's unix socket isn't an option). Serves the same
// GET /status and GET /events over TCP, with plausibly wandering temperatures.
//
//   node scripts/mock-daemon.mjs [port]            then run the app with FANCONTROL_URL=http://127.0.0.1:5178
//
// Type a letter + Enter to inject a fault: s = stall a fan, g = GPU unreadable,
// u = loop unhealthy, r = grow a drive's reallocated count, c = clear all faults.

import http from 'node:http';

const port = Number(process.argv[2] ?? 5178);
const POLL_MS = 2000;

const faults = { stall: false, gpu: false, unhealthy: false };
let reallocated = 1048;
let driveHealthAsOf = new Date().toISOString();

const drives = Array.from({ length: 13 }, (_, i) => ({
	wwn: `naa.5000c500${(0xa0000000 + i * 0x1111111).toString(16)}`,
	device: `sd${String.fromCharCode(97 + i)}`,
	base: 30 + ((i * 7) % 9),
	ssd: i === 3 || i === 12
}));

const fans = [
	{ id: 'drive-cage', rpmPerPercent: 18.6 },
	{ id: 'cpu-cooler-front', rpmPerPercent: 17.2 },
	{ id: 'cpu-cooler-rear', rpmPerPercent: 17.6 },
	{ id: 'intake-cpu', rpmPerPercent: 16.4 },
	{ id: 'intake-gpu-lsi', rpmPerPercent: 12.7 },
	{ id: 'lsi-cooling', rpmPerPercent: 56 }
];

// A slow load cycle plus noise, so the charts have some shape to them.
const wave = (periodSeconds, phase = 0) => Math.sin((Date.now() / 1000 / periodSeconds) * 2 * Math.PI + phase);
const noise = (amount) => (Math.random() - 0.5) * amount;
const duty = (celsius, low, high) => Math.round(Math.min(100, Math.max(30, 30 + ((celsius - low) / (high - low)) * 70)));

function snapshot() {
	const cpu = 46 + wave(600) * 12 + noise(1.5);
	const gpu = 40 + wave(900, 1) * 6 + noise(1);
	const hba = 54 + wave(1200, 2) * 3 + noise(0.4);
	const driveTemps = drives.map((d) => Math.round(d.base + wave(1800, d.base) * 2 + noise(0.6)));
	const hottestDrive = Math.max(...driveTemps);

	const reading = (id, category, label, celsius) => ({
		id,
		category,
		label,
		celsiusOrNull: celsius === null ? null : Math.round(celsius * 100) / 100,
		sourcePath: 'mock',
		isAvailable: celsius !== null
	});

	const duties = {
		'drive-cage': duty(hottestDrive, 28, 48),
		'cpu-cooler-front': duty(cpu, 30, 75),
		'cpu-cooler-rear': duty(cpu, 30, 75),
		'intake-cpu': duty(cpu, 30, 80),
		'intake-gpu-lsi': faults.gpu ? 80 : duty(Math.max(gpu, hba), 40, 85),
		'lsi-cooling': duty(Math.max(hba, hottestDrive), 45, 75)
	};

	return {
		timestampUtc: new Date().toISOString(),
		sensors: [
			reading('cpu', 'cpu', 'Tctl', cpu),
			reading('board', 'boardAmbient', 'SYSTIN', 33 + wave(2400) + noise(0.3)),
			...drives.map((d, i) => reading(`drive:${d.wwn}`, 'drive', d.wwn, driveTemps[i])),
			reading('dimm:hwmon15', 'memory', 'hwmon15', 32 + wave(1500) * 1.5),
			reading('dimm:hwmon16', 'memory', 'hwmon16', 33 + wave(1500, 1) * 1.5),
			reading('gpu', 'gpu', 'GPU', faults.gpu ? null : gpu),
			reading('hba', 'hba', 'IOC Temperature', hba)
		],
		fans: fans.map((fan) => {
			const stalled = faults.stall && fan.id === 'drive-cage';
			return {
				id: fan.id,
				dutyPercent: duties[fan.id],
				rpm: stalled ? 0 : Math.round(duties[fan.id] * fan.rpmPerPercent + noise(20)),
				mode: 'manual',
				stalled
			};
		}),
		driveHealth: drives.map((d, i) => ({
			deviceName: d.wwn,
			passed: true,
			reallocatedSectorCount: i === 2 ? reallocated : 0,
			pendingSectorCount: d.ssd ? null : 0,
			powerOnHours: 9000 + i * 1300,
			sourcePath: `/dev/${d.device}`,
			isAvailable: true,
			asOf: driveHealthAsOf
		})),
		controlLoopHealthy: !faults.unhealthy
	};
}

const clients = new Set();
setInterval(() => {
	const event = `event: status\ndata: ${JSON.stringify(snapshot())}\n\n`;
	for (const client of clients) client.write(event);
}, POLL_MS);

http
	.createServer((request, response) => {
		if (request.url === '/status') {
			response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(snapshot()));
		} else if (request.url === '/events') {
			response.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache' });
			response.write(`event: status\ndata: ${JSON.stringify(snapshot())}\n\n`);
			clients.add(response);
			request.on('close', () => clients.delete(response));
		} else {
			response.writeHead(404).end();
		}
	})
	.listen(port, '127.0.0.1', () => console.log(`mock fancontrol daemon on http://127.0.0.1:${port} (s/g/u/r/c + Enter to inject faults)`));

process.stdin.on('data', (input) => {
	const key = input.toString().trim();
	if (key === 's') faults.stall = true;
	else if (key === 'g') faults.gpu = true;
	else if (key === 'u') faults.unhealthy = true;
	else if (key === 'r') {
		reallocated += 8;
		driveHealthAsOf = new Date().toISOString();
	} else if (key === 'c') faults.stall = faults.gpu = faults.unhealthy = false;
	console.log('faults:', faults, 'reallocated:', reallocated);
});
