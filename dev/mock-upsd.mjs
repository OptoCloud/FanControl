// A stand-in for NUT's upsd, for developing vigil-core without the UPS. Answers
// LIST VAR and GET NUMLOGINS for one UPS ("apc") with the variables apc_modbus reports for
// orion's APC Smart-UPS 1000, over the real line protocol.
//
//   node dev/mock-upsd.mjs [port]            then run vigil-core with NUT_HOST=127.0.0.1 NUT_PORT=3493
//
// Type a letter + Enter: b = mains lost (on battery), l = low battery, r = replace battery,
// s = driver loses the UPS (ERR DATA-STALE), m = upsmon dies (no monitors), h = hot battery,
// c = back to normal.

import net from 'node:net';

const port = Number(process.argv[2] ?? 3493);
const UPS = 'apc';

const state = { onBattery: false, low: false, replace: false, stale: false, unguarded: false, hot: false, charge: 100 };

setInterval(() => {
	if (state.onBattery) state.charge = Math.max(5, state.charge - 0.5);
	else state.charge = Math.min(100, state.charge + 0.2);
}, 1000);

const wave = (periodSeconds) => Math.sin((Date.now() / 1000 / periodSeconds) * 2 * Math.PI);
const noise = (amount) => (Math.random() - 0.5) * amount;

function variables() {
	const load = Math.round(24 + wave(600) * 6 + noise(2));
	const flags = [state.onBattery ? 'OB DISCHRG' : state.charge < 100 ? 'OL CHRG' : 'OL'];
	if (state.low || state.charge < 10) flags.push('LB');
	if (state.replace) flags.push('RB');

	return {
		'battery.charge': state.charge.toFixed(0),
		'battery.charge.low': '30',
		'battery.date': '2026-08-15',
		'battery.runtime': String(Math.round((state.charge / 100) * 2400 * (25 / Math.max(5, load)))),
		'battery.runtime.low': '600',
		'battery.temperature': (state.hot ? 43.2 : 34.2 + noise(0.2)).toFixed(2),
		'battery.type': 'PbAc',
		'battery.voltage': (state.onBattery ? 25.1 : 27.3 + noise(0.1)).toFixed(1),
		'battery.voltage.nominal': '24.0',
		'device.mfr': 'American Power Conversion',
		'device.model': 'Smart-UPS 1000',
		'device.serial': 'AS1234567890',
		'device.type': 'ups',
		'driver.flag.ignorelb': 'enabled',
		'driver.name': 'apc_modbus',
		'input.voltage': state.onBattery ? '0.0' : (231 + wave(1800) * 3 + noise(1.5)).toFixed(1),
		'input.voltage.nominal': '230',
		'input.transfer.reason': state.onBattery ? 'LowInputVoltage' : 'AcceptableInput',
		'output.current': (load / 30).toFixed(2),
		'output.frequency': (49.97 + noise(0.04)).toFixed(2),
		'output.voltage': (230 + noise(0.8)).toFixed(1),
		'ups.load': String(load),
		'ups.mfr': 'American Power Conversion',
		'ups.model': 'Smart-UPS 1000',
		'ups.realpower.nominal': '670',
		'ups.status': flags.join(' '),
		'ups.test.result': 'No test initiated'
	};
}

const quote = (value) => `"${value.replace(/[\\"]/g, '\\$&')}"`;

net
	.createServer((socket) => {
		let buffer = '';
		socket.setEncoding('utf8');
		socket.on('data', (chunk) => {
			buffer += chunk;
			let newline;
			while ((newline = buffer.indexOf('\n')) >= 0) {
				const line = buffer.slice(0, newline).trim();
				buffer = buffer.slice(newline + 1);
				const [command, sub, name] = line.split(/\s+/);

				if (command === 'LOGOUT') {
					socket.end('OK Goodbye\n');
				} else if (command === 'LIST' && sub === 'VAR') {
					if (name !== UPS) socket.write('ERR UNKNOWN-UPS\n');
					else if (state.stale) socket.write('ERR DATA-STALE\n');
					else {
						const body = Object.entries(variables()).map(([key, value]) => `VAR ${UPS} ${key} ${quote(value)}\n`);
						socket.write(`BEGIN LIST VAR ${UPS}\n${body.join('')}END LIST VAR ${UPS}\n`);
					}
				} else if (command === 'GET' && sub === 'NUMLOGINS') {
					socket.write(name === UPS ? `NUMLOGINS ${UPS} ${state.unguarded ? 0 : 1}\n` : 'ERR UNKNOWN-UPS\n');
				} else {
					socket.write('ERR UNKNOWN-COMMAND\n');
				}
			}
		});
		socket.on('error', () => {});
	})
	.listen(port, '127.0.0.1', () => console.log(`mock upsd on 127.0.0.1:${port}, UPS "${UPS}" (b/l/r/s/m/h/c + Enter)`));

process.stdin.on('data', (input) => {
	const key = input.toString().trim();
	if (key === 'b') state.onBattery = true;
	else if (key === 'l') state.low = true;
	else if (key === 'r') state.replace = true;
	else if (key === 's') state.stale = true;
	else if (key === 'm') state.unguarded = true;
	else if (key === 'h') state.hot = true;
	else if (key === 'c') Object.assign(state, { onBattery: false, low: false, replace: false, stale: false, unguarded: false, hot: false });
	console.log('state:', state);
});
