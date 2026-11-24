let asuisuiReloadingJobId = null;

function compareBridgeVersions(left, right) {
	const a = left.split(".").map(Number);
	const b = right.split(".").map(Number);
	if (a.some(Number.isNaN) || b.some(Number.isNaN)) return left === right ? 0 : -1;
	for (let i = 0; i < Math.max(a.length, b.length); i++) {
		const difference = (a[i] || 0) - (b[i] || 0);
		if (difference !== 0) return Math.sign(difference);
	}
	return 0;
}

async function ensureCurrentBridgeVersion(job) {
	const expectedVersion = job?.extensionVersion;
	const installedVersion = chrome.runtime.getManifest().version;
	if (!expectedVersion || compareBridgeVersions(installedVersion, expectedVersion) >= 0) return true;
	if (asuisuiReloadingJobId === job.id) return false;
	asuisuiReloadingJobId = job.id;
	try {
		const result = await chrome.runtime.sendMessage({
			type: "reload-extension",
			payload: { jobId: job.id, expectedVersion }
		});
		if (!result?.ok) {
			asuisuiReloadingJobId = null;
			console.error("Asuisui extension auto-reload was rejected", result?.reason || result?.error || "unknown reason");
		}
	} catch (error) {
		asuisuiReloadingJobId = null;
		console.error("Asuisui extension auto-reload failed", error);
	}
	return false;
}
