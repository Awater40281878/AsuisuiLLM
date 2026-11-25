const inputSelectors = [
	"[contenteditable='true'][aria-label='提出問題']",
	"[contenteditable='true'][aria-label='Ask anything']",
	"[contenteditable='true'][role='textbox']",
	"[contenteditable='true']",
	"[contenteditable]",
	"[role='textbox']",
	"textarea[aria-label='提出問題']",
	"textarea[placeholder='提出問題']",
	"textarea[aria-label='Ask anything']",
	"textarea[placeholder='Ask anything']",
	"textarea"
];

let seenJobId = null;
let active = false;

function isVisible(element) {
	const rect = element.getBoundingClientRect();
	const style = getComputedStyle(element);
	return rect.width > 0 && rect.height > 0 && style.visibility !== "hidden" && style.display !== "none";
}

function findPrompt() {
	for (const selector of inputSelectors) {
		const element = [...document.querySelectorAll(selector)].find(candidate =>
			isVisible(candidate) && !candidate.disabled && !candidate.readOnly &&
			(candidate.isContentEditable || "value" in candidate || candidate.getAttribute("role") === "textbox"));
		if (element) return element;
	}
	return null;
}

function readMainText() {
	return (document.querySelector("main")?.innerText || document.body?.innerText || "").trim();
}

async function waitForPrompt() {
	for (let i = 0; i < 60; i++) {
		const prompt = findPrompt();
		if (prompt) return prompt;
		await new Promise(resolve => setTimeout(resolve, 500));
	}
	throw new Error("Google AI Mode input was not found.");
}

function setNativeValue(element, value) {
	if (element.isContentEditable) {
		element.textContent = value;
	} else {
		const prototype = element instanceof HTMLTextAreaElement
			? HTMLTextAreaElement.prototype
			: HTMLInputElement.prototype;
		const setter = Object.getOwnPropertyDescriptor(prototype, "value")?.set;
		if (setter) setter.call(element, value);
		else element.value = value;
	}
	element.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "insertText", data: value }));
	element.dispatchEvent(new Event("change", { bubbles: true }));
}

function promptValue(element) {
	return element.isContentEditable ? (element.innerText || element.textContent || "").trim() : String(element.value || "").trim();
}

function submitPrompt(element) {
	const form = element.closest("form");
	const buttons = [...document.querySelectorAll("button")].filter(button =>
		isVisible(button) && !button.disabled);
	const submit = buttons.find(button => {
		const label = `${button.getAttribute("aria-label") || ""} ${button.innerText || ""}`;
		return /^(send|submit|search|ask|提出問題|傳送|送出|發送|搜尋|搜索|提交)$/i.test(label.trim());
	}) || buttons.find(button => {
		const label = `${button.getAttribute("aria-label") || ""} ${button.innerText || ""}`;
		return /send|submit|傳送|送出|發送|提交/i.test(label);
	}) || buttons.find(button => form && button.form === form && button.type === "submit");
	if (submit) {
		submit.click();
		return true;
	}
	if (form && typeof form.requestSubmit === "function") {
		form.requestSubmit();
		return true;
	}
	for (const type of ["keydown", "keypress", "keyup"])
		element.dispatchEvent(new KeyboardEvent(type, { key: "Enter", code: "Enter", keyCode: 13, which: 13, bubbles: true }));
	return true;
}

async function waitForSubmission(element, query) {
	for (let i = 0; i < 60; i++) {
		await new Promise(resolve => setTimeout(resolve, 200));
		const currentPrompt = findPrompt();
		const remaining = currentPrompt ? promptValue(currentPrompt) : "";
		const mainText = readMainText();
		if (remaining !== query && mainText.includes(query)) return;
		if (!remaining && mainText.length > query.length + 100) return;
	}
	throw new Error("Google AI Mode 的問題仍留在輸入框，沒有確認送出；搜尋頁會保留供檢查。");
}

function collectSources(root) {
	const seen = new Set();
	return [...root.querySelectorAll("a[href^='http']")]
		.map(anchor => ({ title: (anchor.innerText || anchor.getAttribute("aria-label") || "").trim(), url: anchor.href }))
		.filter(source => {
			try {
				const host = new URL(source.url).hostname;
				if (!source.title || !/^https?:/.test(source.url) || host === "google.com" || host.endsWith(".google.com") || seen.has(source.url)) return false;
				seen.add(source.url);
				return true;
			} catch { return false; }
		});
}

function answerAfterQuestion(text, query) {
	const position = text.lastIndexOf(query);
	let answer = position >= 0 ? text.slice(position + query.length) : text;
	answer = answer.replace(/^\s*[」』"']?\s*的回覆\s*/i, "").replace(/\n{3,}/g, "\n\n").trim();
	const footer = answer.search(/AI 可能會出錯|AI responses may include mistakes|相關問題|Related questions/i);
	if (footer > 120) answer = answer.slice(0, footer).trim();
	const controls = answer.search(/跳至上一個提示詞|AI 模式回應已就緒|All items removed from input context/i);
	if (controls >= 0) answer = answer.slice(0, controls).trim();
	return answer.slice(0, 4500);
}

async function report(type, payload) {
	try { return await chrome.runtime.sendMessage({ type, payload }); }
	catch { return null; }
}

async function attachImage(job) {
	void report("diagnostic", { jobId: job.id, stage: "image-attachment-started" });
	const bytes = Uint8Array.from(atob(job.imageBase64), c => c.charCodeAt(0));
	const file = new File([bytes], "asuisui-screen.png", { type: "image/png" });
	let input = [...document.querySelectorAll('input[type="file"]')].find(node => !node.disabled && (!node.accept || /image|\*/i.test(node.accept)));
	if (!input) {
		const menu = [...document.querySelectorAll('button[aria-haspopup]')].find(button => isVisible(button) && /add|attach|upload|新增|附加|上傳|圖片|图像|添加/i.test(`${button.getAttribute("aria-label") || ""} ${button.innerText || ""}`));
		if (menu) menu.click();
		for (let i = 0; i < 20 && !input; i++) {
			await new Promise(resolve => setTimeout(resolve, 250));
			input = [...document.querySelectorAll('input[type="file"]')].find(node => !node.disabled && (!node.accept || /image|\*/i.test(node.accept)));
		}
	}
	if (!input) throw new Error("找不到 Google AI 圖片上傳欄位；請檢查目前網頁是否支援上傳圖片。");
	void report("diagnostic", { jobId: job.id, stage: "image-input-found" });
	const existingImages = new Set(document.querySelectorAll('img'));
	const existingButtons = new Set(document.querySelectorAll('button'));
	const transfer = new DataTransfer(); transfer.items.add(file); input.files = transfer.files;
	input.dispatchEvent(new Event("input", { bubbles: true }));
	input.dispatchEvent(new Event("change", { bubbles: true }));
	for (let i = 0; i < 120; i++) {
		await new Promise(resolve => setTimeout(resolve, 250));
		const preview = [...document.querySelectorAll('img')].some(node => !existingImages.has(node) && isVisible(node) && /^(blob:|data:)/i.test(node.currentSrc || node.src || ""));
		const remove = [...document.querySelectorAll('button')].some(node => !existingButtons.has(node) && isVisible(node) && /remove|delete|移除|刪除|删除/i.test(`${node.getAttribute("aria-label") || ""} ${node.title || ""}`));
		if (preview || remove) { void report("diagnostic", { jobId: job.id, stage: "image-preview-confirmed" }); return; }
	}
	throw new Error("圖片已交給網頁，但未確認預覽載入；為避免送出沒有圖片的問題，本次已停止。");
}

async function watchForAnswer(job, baseline) {
	let previous = "";
	let stableReads = 0;
	for (let i = 0; i < 180; i++) {
		await new Promise(resolve => setTimeout(resolve, 1000));
		const text = readMainText();
		const changed = job.hasImage
			? text.includes(job.query) && text.length > job.query.length + 20
			: text.length > baseline.length + 60 || (text.includes(job.query) && text.length > job.query.length + 180);
		if (changed && text.length > (job.hasImage ? 30 : 180) && text === previous) stableReads++;
		else stableReads = 0;
		if (stableReads >= 5) {
			const root = document.querySelector("main") || document.body;
			const answer = answerAfterQuestion(text, job.query);
			if (answer.length < (job.hasImage ? 10 : 80)) continue;
			await report("result", {
				jobId: job.id,
				query: job.query,
				answer,
				sources: collectSources(root)
			});
			await chrome.storage.session.remove("asuisuiActiveJob");
			return;
		}
		previous = text;
	}
	await report("result", { jobId: job.id, query: job.query, error: "Google AI Mode answer timed out." });
	await chrome.storage.session.remove("asuisuiActiveJob");
}

async function runJob(job) {
	if (active || !job?.id || !job?.query || job.id === seenJobId) return;
	active = true;
	seenJobId = job.id;
	try {
		console.info("Asuisui bridge received a Google AI Mode job.");
		let prompt = await waitForPrompt();
		const baseline = readMainText();
		if (job.imageBase64) { await attachImage(job); prompt = await waitForPrompt(); }
		const { imageBase64, ...savedJob } = job;
		job.hasImage = !!imageBase64;
		await chrome.storage.session.set({ asuisuiActiveJob: { ...savedJob, hasImage: job.hasImage, baseline } });
		prompt.focus();
		setNativeValue(prompt, job.query);
		await new Promise(resolve => setTimeout(resolve, 120));
		if (promptValue(prompt) !== job.query) throw new Error("Google AI Mode 輸入框未收到完整問題。");
		submitPrompt(prompt);
		await waitForSubmission(prompt, job.query);
		await report("submitted", { jobId: job.id });
		await watchForAnswer(job, baseline);
	} catch (error) {
		console.error("Asuisui Google AI Mode page automation failed", error);
		await report("result", { jobId: job.id, query: job.query, error: String(error) });
		await chrome.storage.session.remove("asuisuiActiveJob");
	} finally {
		active = false;
	}
}

async function resumeSavedJob() {
	let saved;
	try { saved = (await chrome.storage.session.get("asuisuiActiveJob")).asuisuiActiveJob; }
	catch { return; }
	if (!saved || saved.provider !== "google" || active || seenJobId === saved.id) return;
	const requestedJobId = location.hash.match(/^#asuisui-job=([^&]+)/)?.[1];
	if (requestedJobId && decodeURIComponent(requestedJobId) !== saved.id) return;
	const owned = await chrome.runtime.sendMessage({ type: "is-owned-job", payload: { jobId: saved.id } });
	if (!owned?.ok) return;
	if (!await ensureCurrentBridgeVersion(saved)) return;
	active = true;
	seenJobId = saved.id;
	try { await watchForAnswer(saved, saved.baseline || ""); }
	finally { active = false; }
}

async function pollForJobs() {
	await resumeSavedJob();
	if (active) return;
	try {
		const jobId = location.hash.match(/^#asuisui-job=([^&]+)/)?.[1];
		if (!jobId) return;
		const job = await chrome.runtime.sendMessage({ type: "poll-job", payload: { jobId: decodeURIComponent(jobId) } });
		if (job?.id && await ensureCurrentBridgeVersion(job)) await runJob(job);
	} catch { }
}

setInterval(pollForJobs, 800);
pollForJobs();
