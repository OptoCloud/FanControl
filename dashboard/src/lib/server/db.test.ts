import { describe, expect, it } from 'vitest';
import type { SensorReading } from '$lib/types';
import { seriesIdsOf } from './db';

const reading = (overrides: Partial<SensorReading>): SensorReading => ({
	id: 'cpu',
	category: 'cpu',
	label: 'Tctl',
	celsiusOrNull: 40,
	sourcePath: 'path',
	isAvailable: true,
	...overrides
});

describe('seriesIdsOf', () => {
	it('stores a drive under its own id and under its bay', () => {
		const drive = reading({ id: 'drive:naa.5000c500bae40598', category: 'drive', port: 'pci-0000:01:00.1-ata-3' });

		expect(seriesIdsOf(drive)).toEqual(['drive:naa.5000c500bae40598', 'port:pci-0000:01:00.1-ata-3']);
	});

	it('stores everything else, and a drive from a daemon without ports, under its id alone', () => {
		expect(seriesIdsOf(reading({}))).toEqual(['cpu']);
		expect(seriesIdsOf(reading({ id: 'drive:naa.1', category: 'drive', port: null }))).toEqual(['drive:naa.1']);
		expect(seriesIdsOf(reading({ id: 'drive:naa.1', category: 'drive' }))).toEqual(['drive:naa.1']);
	});
});
