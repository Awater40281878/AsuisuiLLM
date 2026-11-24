const bridgeRoot = "http://127.0.0.1:43829";
const bridgeToken = "asuisui-ggo-bridge-v1-7f2f3fd64fcb45a1b313d33d0eccaf27";
chrome.storage.session.setAccessLevel({ accessLevel: "TRUSTED_AND_UNTRUSTED_CONTEXTS" }).catch(() => {});

chrome.runtime.onInstalled.addListener(details => {
	chrome.storage.session.setAccessLevel({ accessLevel: "TRUSTED_AND_UNTRUSTED_CONTEXTS" });
	if (details.reason === "update") void reportReloadReady();
});
chrome.runtime.onStartup.addListener(() => {
	chrome.storage.session.setAccessLevel({ accessLevel: "TRUSTED_AND_UNTRUSTED_CONTEXTS" });
});

async function reportReloadReady() {
	try {
		const { asuisuiManagedSearchJobId } = await chrome.storage.local.get("asuisuiManagedSearchJobId");
		if (!asuisuiManagedSearchJobId) return;
		const response = await fetch(`${bridgeRoot}/reload-ready?token=${encodeURIComponent(bridgeToken)}`, {
			method: "POST",
			headers: { "Content-Type": "text/plain;charset=UTF-8" },
			body: JSON.stringify({
				jobId: asuisuiManagedSearchJobId,
				extensionVersion: chrome.runtime.getManifest().version
			}),
			targetAddressSpace: "loopback"
		});
	} catch { }
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
	if (message?.type === "is-owned-job") {
		chrome.storage.local.get(["asuisuiManagedSearchTabId", "asuisuiManagedSearchJobId"])
			.then(managed => sendResponse({ ok: managed.asuisuiManagedSearchTabId === sender.tab?.id && managed.asuisuiManagedSearchJobId === message.payload?.jobId }))
			.catch(() => sendResponse({ ok: false }));
		return true;
	}
	if (message?.type === "reload-extension") {
		const currentVersion = chrome.runtime.getManifest().version;
		const payload = message.payload || {};
		if (!sender.tab?.id || !payload.jobId || !payload.expectedVersion || payload.expectedVersion === currentVersion) {
			sendResponse({ ok: false, reason: "reload not required" });
			return false;
		}
		fetch(`${bridgeRoot}/reload?token=${encodeURIComponent(bridgeToken)}`, {
			method: "POST",
			headers: { "Content-Type": "text/plain;charset=UTF-8" },
			body: JSON.stringify({
				jobId: payload.jobId,
				expectedVersion: payload.expectedVersion,
				installedVersion: currentVersion
			}),
			targetAddressSpace: "loopback"
		})
			.then(async response => {
				sendResponse({ ok: response.ok });
				if (response.ok) setTimeout(() => chrome.runtime.reload(), 300);
			})
			.catch(error => sendResponse({ ok: false, error: String(error) }));
		return true;
	}

	if (message?.type === "poll-job") {
		const requestedJobId = message.payload?.jobId;
		if (!requestedJobId || !sender.tab?.id) {
			sendResponse(null);
			return false;
		}
		fetch(`${bridgeRoot}/job?token=${encodeURIComponent(bridgeToken)}&jobId=${encodeURIComponent(requestedJobId)}`, {
			cache: "no-store",
			targetAddressSpace: "loopback"
		})
			.then(async response => {
				if (!response.ok && response.status !== 204) {
					const error = await response.text();
					console.error("Asuisui bridge poll failed", response.status, error);
					return null;
				}
				return response.status === 204 ? null : await response.json();
			})
			.then(async job => {
				if (job?.id === requestedJobId) {
					const previous = await chrome.storage.local.get(["asuisuiManagedSearchTabId", "asuisuiManagedSearchJobId"]);
					if (Number.isInteger(previous.asuisuiManagedSearchTabId) && previous.asuisuiManagedSearchTabId !== sender.tab.id)
						await closeOwnedSearchTab(previous.asuisuiManagedSearchTabId, previous.asuisuiManagedSearchJobId);
					await chrome.storage.local.set({ asuisuiManagedSearchTabId: sender.tab.id, asuisuiManagedSearchJobId: requestedJobId });
				}
				sendResponse(job);
			})
			.catch(error => {
				console.error("Asuisui bridge poll request failed", error);
				sendResponse(null);
			});
		return true;
	}

	if (message?.type === "diagnostic" || message?.type === "submitted" || message?.type === "result") {
		const route = message.type === "diagnostic" ? "/diagnostic" : message.type === "submitted" ? "/submitted" : "/result";
		fetch(`${bridgeRoot}${route}?token=${encodeURIComponent(bridgeToken)}`, {
			method: "POST",
			headers: { "Content-Type": "text/plain;charset=UTF-8" },
			body: JSON.stringify(message.payload)
		})
			.then(async response => {
				if (response.ok && message.type === "result" && !message.payload?.error)
					await closeOwnedSearchTab(sender.tab?.id, message.payload?.jobId);
				sendResponse({ ok: response.ok });
			})
			.catch(error => sendResponse({ ok: false, error: String(error) }));
		return true;
	}
});

async function closeOwnedSearchTab(tabId, jobId) {
	if (!Number.isInteger(tabId)) return;
	try {
		const managed = await chrome.storage.local.get(["asuisuiManagedSearchTabId", "asuisuiManagedSearchJobId"]);
		if (managed.asuisuiManagedSearchTabId !== tabId || (jobId && managed.asuisuiManagedSearchJobId !== jobId)) return;
		const tab = await chrome.tabs.get(tabId);
		const url = new URL(tab.url || "");
		const isGoogle = url.hostname === "google.com" || url.hostname === "www.google.com";
		const isChatGpt = url.hostname === "chatgpt.com" || url.hostname === "www.chatgpt.com";
		if (!(isChatGpt || (isGoogle && ["/ai", "/search"].includes(url.pathname)))) return;
		await chrome.tabs.remove(tabId);
		await chrome.storage.local.remove(["asuisuiManagedSearchTabId", "asuisuiManagedSearchJobId"]);
	} catch { }
}
