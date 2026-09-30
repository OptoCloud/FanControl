import net from 'node:net';
import { afterEach, describe, expect, it } from 'vitest';
import type { UpsReading } from '$lib/types';
import { NutClient, parseVariables, toReading } from './nut';

describe('parseVariables', () => {
	it('reads VAR lines and skips the BEGIN/END framing', () => {
		expect(
			parseVariables(['BEGIN LIST VAR apc', 'VAR apc battery.charge "100"', 'VAR apc ups.status "OL CHRG"', 'END LIST VAR apc'])
		).toEqual({ 'battery.charge': '100', 'ups.status': 'OL CHRG' });
	});

	it('unescapes quotes and backslashes in values', () => {
		expect(parseVariables(['VAR apc device.model "Smart \\"UPS\\" C:\\\\1000"'])).toEqual({ 'device.model': 'Smart "UPS" C:\\1000' });
	});

	it('keeps an empty value', () => {
		expect(parseVariables(['VAR apc ups.test.result ""'])).toEqual({ 'ups.test.result': '' });
	});
});

describe('toReading', () => {
	const at = new Date('2026-09-30T12:00:00.000Z');

	it('picks out the metrics and splits the status flags', () => {
		const reading = toReading(
			'apc',
			{
				'battery.charge': '87',
				'battery.runtime': '2310',
				'ups.load': '25',
				'ups.realpower.nominal': '670',
				'input.voltage': '231.4',
				'output.voltage': '230.0',
				'battery.voltage': '27.3',
				'ups.status': 'OB DISCHRG',
				'device.mfr': 'American Power Conversion',
				'device.model': 'Smart-UPS 1000'
			},
			at
		);

		expect(reading).toMatchObject({
			timestampUtc: '2026-09-30T12:00:00.000Z',
			name: 'apc',
			model: 'American Power Conversion Smart-UPS 1000',
			status: ['OB', 'DISCHRG'],
			batteryCharge: 87,
			batteryRuntimeSeconds: 2310,
			load: 25,
			realPower: 168,
			inputVoltage: 231.4,
			outputVoltage: 230,
			batteryVoltage: 27.3
		});
	});

	it('prefers a reported real power over the derived one, and leaves missing metrics null', () => {
		const reading = toReading('apc', { 'ups.realpower': '150', 'ups.load': '25', 'ups.realpower.nominal': '670', 'input.voltage': 'n/a' }, at);
		expect(reading.realPower).toBe(150);
		expect(reading.inputVoltage).toBeNull();
		expect(reading.batteryCharge).toBeNull();
		expect(reading.status).toEqual([]);
		expect(reading.model).toBeNull();
	});
});

describe('NutClient', () => {
	let server: net.Server | null = null;
	let client: NutClient | null = null;

	afterEach(() => {
		client?.stop();
		server?.close();
	});

	/** A one-UPS upsd that answers every LIST VAR with `answer()`. */
	async function fakeUpsd(answer: () => string): Promise<number> {
		server = net.createServer((socket) => {
			socket.on('data', (chunk) => {
				for (const line of chunk.toString().split('\n').filter(Boolean)) {
					if (line.startsWith('LIST VAR')) socket.write(answer());
				}
			});
			socket.on('error', () => {});
		});
		await new Promise<void>((resolve) => server!.listen(0, '127.0.0.1', resolve));
		return (server.address() as net.AddressInfo).port;
	}

	it('polls LIST VAR and reports readings, even when an answer arrives in pieces', async () => {
		const port = await fakeUpsd(() => 'BEGIN LIST VAR apc\r\nVAR apc battery.charge "100"\nVAR apc ups.status "OL"\nEND LIST VAR apc\n');
		const readings: UpsReading[] = [];

		await new Promise<void>((resolve) => {
			client = new NutClient(
				{ host: '127.0.0.1', port, ups: 'apc', pollIntervalMs: 20 },
				{
					onReading: (reading) => {
						readings.push(reading);
						if (readings.length === 2) resolve();
					},
					onError: () => {}
				}
			);
			client.start();
		});

		expect(readings[0].batteryCharge).toBe(100);
		expect(readings[0].status).toEqual(['OL']);
	});

	it("reports upsd's ERR answers as errors and keeps polling", async () => {
		const port = await fakeUpsd(() => 'ERR DATA-STALE\n');
		const errors: string[] = [];

		await new Promise<void>((resolve) => {
			client = new NutClient(
				{ host: '127.0.0.1', port, ups: 'apc', pollIntervalMs: 20 },
				{
					onReading: () => {},
					onError: (error) => {
						errors.push(error);
						if (errors.length === 2) resolve();
					}
				}
			);
			client.start();
		});

		expect(errors).toEqual(['upsd: DATA-STALE', 'upsd: DATA-STALE']);
	});

	it('reports an unreachable upsd as an error', async () => {
		const port = await fakeUpsd(() => '');
		await new Promise<void>((resolve) => server!.close(() => resolve()));
		server = null;

		const error = await new Promise<string>((resolve) => {
			client = new NutClient({ host: '127.0.0.1', port, ups: 'apc', pollIntervalMs: 20 }, { onReading: () => {}, onError: resolve });
			client.start();
		});
		expect(error).toMatch(/ECONNREFUSED/);
	});
});
