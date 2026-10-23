const interpretDevice = (device) => {
	const calibration = device?.calibration;
	const quilt = device?.defaultQuilt;
	const valid = calibration != null && quilt != null
		&& ["DPI", "screenW", "screenH", "pitch", "slope", "center", "invView", "viewCone"]
			.every((key) => Number.isFinite(calibration[key]?.value))
		&& calibration.DPI.value > 0 && calibration.screenW.value > 0 && calibration.screenH.value > 0
		&& calibration.slope.value !== 0
		&& ["tileX", "tileY", "quiltX", "quiltY", "quiltAspect"].every((key) => Number.isFinite(quilt[key]) && quilt[key] > 0)
		&& Number.isInteger(quilt.tileX) && Number.isInteger(quilt.tileY);
	if (!valid) {
		return { enabled: false, tileX: 1, tileY: 1 };
	}

	const fov = 15;

	const values = Object.fromEntries(
		Object.entries(device.calibration)
			.map(([key, value]) => [key, value.value])
			.filter(([key, value]) => value != null)
	);

	const screenInches = values.screenW / values.DPI;
	const pitch = values.pitch * screenInches * Math.cos(Math.atan(1.0 / values.slope));
	const tilt = (values.screenH / (values.screenW * values.slope)) * -((values.flipImageX ?? 0) * 2 - 1);
	const subp = 1 / (values.screenW * 3);

	const defaultQuilt = device.defaultQuilt;

	const quiltViewPortion = [
		(Math.floor(defaultQuilt.quiltX / defaultQuilt.tileX) * defaultQuilt.tileX) / defaultQuilt.quiltX,
		(Math.floor(defaultQuilt.quiltY / defaultQuilt.tileY) * defaultQuilt.tileY) / defaultQuilt.quiltY,
	];

	return {
		...defaultQuilt,
		...values,
		pitch,
		tilt,
		subp,

		quiltViewPortion,
		fov,
		enabled: true,
	};
};

export default async (useHoloplay = false, { loadCore = () => import("../../lib/holoplaycore.module.js"), timeoutMs = 3000 } = {}) => {
	if (!useHoloplay) return interpretDevice(null);
	let client;
	try {
		const HoloPlayCore = await loadCore();
		const device = await new Promise((resolve) => {
			let settled = false;
			const settle = (value) => {
				if (settled) return;
				settled = true;
				clearTimeout(timer);
				resolve(value);
			};
			const timer = setTimeout(() => settle(null), timeoutMs);
			try {
				client = new HoloPlayCore.Client((data) => settle(data.devices?.[0]), () => settle(null));
			} catch { settle(null); }
		});
		const result = interpretDevice(device);
		if (!result.enabled) console.warn("No calibrated Looking Glass device is available; rendering normal Matrix rain.");
		return result;
	} catch (error) {
		console.warn("Looking Glass discovery failed; rendering normal Matrix rain.", error);
		return interpretDevice(null);
	} finally {
		try { client?.disconnect(); } catch { /* Discovery is already complete. */ }
	}
};
