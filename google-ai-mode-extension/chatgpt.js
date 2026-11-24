let seenJobId = null;
let active = false;

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

function findComposer() {
	const selectors = [
		"main #prompt-textarea",
		"main [contenteditable='true'][role='textbox']",
		"main [contenteditable='true'][data-placeholder]",
		"main textarea[placeholder]",
		"#prompt-textarea",
		"[contenteditable='true'][role='textbox']",
		"[contenteditable='true'][data-placeholder]",
		"textarea[placeholder]"
	];
	for (const selector of selectors) {
		const element = [...document.querySelectorAll(selector)].find(candidate => {
			const rect = candidate.getBoundingClientRect();
			const style = getComputedStyle(candidate);
			return rect.width > 0 && rect.height > 0 && style.visibility !== "hidden" && style.display !== "none" &&
				!candidate.disabled && !candidate.readOnly && candidate.getAttribute("aria-disabled") !== "true" &&
				(candidate.isContentEditable || "value" in candidate);
		});
		if (element) return element;
	}
	return null;
}

function readPrompt(element) {
	return element.isContentEditable ? (element.innerText || element.textContent || "").trim() : String(element.value || "").trim();
}

function normalizePrompt(text) {
	return text.replace(/\u00a0/g, " ").replace(/\s+/g, " ").trim();
}

function replacePromptText(element, text) {
	element.focus();
	if (element.isContentEditable) {
		const selection = window.getSelection();
		const range = document.createRange();
		range.selectNodeContents(element);
		selection.removeAllRanges();
		selection.addRange(range);
		element.dispatchEvent(new InputEvent("beforeinput", { bubbles: true, cancelable: true, inputType: "insertReplacementText", data: text }));
		document.execCommand("delete", false);
		document.execCommand("insertText", false, text);
	} else {
		// Prefer the browser editing command so React-backed composers receive the
		// same beforeinput/input sequence as real typing. Fall back to the native
		// textarea setter on browsers/pages that don't support insertText here.
		document.execCommand("selectAll", false, null);
		document.execCommand("insertText", false, text);
		if (readPrompt(element) !== text) {
			const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value")?.set;
			if (setter) setter.call(element, text);
			else element.value = text;
		}
		// execCommand can update the DOM without notifying React on every ChatGPT
		// composer variant, so always emit the bubbling input event as well.
		element.dispatchEvent(new InputEvent("input", { bubbles: true, cancelable: true, inputType: "insertText", data: text }));
	}
	if (element.isContentEditable)
		element.dispatchEvent(new InputEvent("input", { bubbles: true, cancelable: true, inputType: "insertText", data: text }));
	element.dispatchEvent(new Event("change", { bubbles: true }));
}

function assistantMessages() {
	const marked = [...document.querySelectorAll('[data-message-author-role="assistant"]')];
	if (marked.length) return marked;
	// Current ChatGPT layouts identify a reply using a visually hidden heading
	// rather than the old data-message-author-role attribute.
	return [...document.querySelectorAll('main h1, main h2, main h3, main h4, main [role="heading"], [role="main"] h1, [role="main"] h2, [role="main"] h3, [role="main"] h4, [role="main"] [role="heading"]')]
		.filter(node => /^(ChatGPT said|ChatGPT 說|ChatGPT说)\s*[:：]?$/i.test((node.innerText || node.textContent || "").trim()))
		.map(heading => {
			let container = heading.parentElement;
			while (container && container !== document.body) {
				if ((container.innerText || "").trim().length >= 30 && !container.querySelector('[contenteditable="true"], textarea, [role="textbox"]')) return container;
				container = container.parentElement;
			}
			return null;
		}).filter(Boolean);
}

function userMessages() {
	return [...document.querySelectorAll('[data-message-author-role="user"]')];
}

function promptIsVisible(promptText, initialUserCount) {
	const expected = normalizePrompt(promptText);
	const matches = node => normalizePrompt(node.innerText || node.textContent || "").includes(expected);
	if (userMessages().slice(initialUserCount).some(matches)) return true;
	// ChatGPT can change its message attributes. Restrict the fallback to a
	// conversation turn, never the editable composer or the entire page.
	return [...document.querySelectorAll('main article, main [data-testid^="conversation-turn-"]')]
		.some(node => !node.querySelector('[data-message-author-role="assistant"]') && matches(node)) ||
		// Some ChatGPT layouts no longer expose the expected turn attributes.
		// Match the complete submitted text in a visible, non-editable element;
		// the composer itself must never count as evidence of submission.
		[...document.querySelectorAll('main div, main p, [role="main"] div, [role="main"] p')].some(node => {
			if (node.closest('[contenteditable="true"], textarea, [role="textbox"], [data-message-author-role="assistant"]')) return false;
			if (node.querySelector('[contenteditable="true"], textarea, [role="textbox"], [data-message-author-role="assistant"]')) return false;
			const rect = node.getBoundingClientRect();
			const style = getComputedStyle(node);
			return rect.width > 0 && rect.height > 0 && style.display !== "none" && style.visibility !== "hidden" &&
				normalizePrompt(node.innerText || node.textContent || "") === expected;
		});
}

function collectSources(root) {
	const seen = new Set();
	return [...root.querySelectorAll('a[href^="http"]')]
		.map(anchor => ({ title: (anchor.innerText || anchor.getAttribute("aria-label") || anchor.title || "").trim(), url: anchor.href }))
		.filter(source => {
			try {
				const url = new URL(source.url);
				if (!source.title || !/^https?:$/.test(url.protocol) || url.hostname === "chatgpt.com" || url.hostname.endsWith(".chatgpt.com") || seen.has(url.href)) return false;
				seen.add(url.href);
				return true;
			} catch { return false; }
		})
		.slice(0, 12);
}

async function waitForComposer() {
	for (let i = 0; i < 60; i++) {
		const composer = findComposer();
		if (composer) return composer;
		await sleep(500);
	}
	throw new Error("找不到 ChatGPT 輸入框；請確認指定 Chrome 設定檔已登入 ChatGPT。");
}

async function submitPrompt(composer, promptText, job) {
    const initialUserCount = userMessages().length;
    replacePromptText(composer, promptText);
    await sleep(500);
    const readback = readPrompt(composer);
    if (normalizePrompt(readback) !== normalizePrompt(promptText))
        throw new Error(`輸入內容不一致：預期 ${promptText.length} 字，實際 ${readback.length} 字`);

    const isUsable = button => {
        if (!button || !button.isConnected || button.disabled || button.getAttribute("aria-disabled") === "true") return false;
        const rect = button.getBoundingClientRect();
        const style = getComputedStyle(button);
        return rect.width > 0 && rect.height > 0 && style.visibility !== "hidden" && style.display !== "none";
    };
    const findSend = () => {
        const form = composer.closest("form");
        const candidates = [...document.querySelectorAll('button[data-testid="send-button"], button[data-testid="composer-submit-button"], button[aria-label], button[title]')];
        const matches = candidates.filter(button =>
            /^(send|send message|傳送|送出|發送|提交)/i.test(`${button.getAttribute("aria-label") || ""} ${button.title || ""}`.trim()) ||
            ["send-button", "composer-submit-button"].includes(button.getAttribute("data-testid"))
        );
        return matches.find(button => form?.contains(button) && isUsable(button)) || matches.find(isUsable);
    };
    let send = null;
    for (let i = 0; i < 24; i++) {
        send = findSend();
        if (send) break;
        await sleep(250);
    }
    diagnostic(job, `submit-ready:composer=${composer.tagName}#${composer.id};send=${send?.getAttribute("data-testid") || send?.getAttribute("aria-label") || "missing"};userCount=${initialUserCount}`);
    if (!send) throw new Error("找不到可見且啟用的 ChatGPT 送出按鈕；可能是 React 未同步輸入狀態。");
    send.click();
    diagnostic(job, "send-clicked");

    await confirmSubmission(composer, promptText, job, initialUserCount);
}

async function confirmSubmission(composer, promptText, job, initialUserCount) {
    // Never treat a cleared composer or a changed URL/title alone as proof of submission.
    for (let i = 0; i < 150; i++) {
        await sleep(200);
        if (promptIsVisible(promptText, initialUserCount)) {
            diagnostic(job, "user-message-confirmed");
            return;
        }
    }
    diagnostic(job, `submit-unconfirmed:path=${location.pathname};userCount=${userMessages().length};turns=${document.querySelectorAll('main article, main [data-testid^="conversation-turn-"]').length};composerLength=${composer ? readPrompt(composer).length : -1}`);
    throw new Error("已點擊送出，但 30 秒內未確認使用者訊息出現在對話中。為避免重複提問，不會自動重送。");
}

async function waitForAnswer(job, previousCount) {
	let previous = "";
	let stableReads = 0;
	let retriedAccountChange = false;
	for (let i = 0; i < 150; i++) {
		await sleep(1000);
		const pageText = document.body?.innerText || "";
		if (/account.{0,20}changed|帳戶.{0,8}(已)?變更|重新傳送訊息/i.test(pageText) && !retriedAccountChange) {
			const retry = [...document.querySelectorAll("button")].find(button => {
				const labels = [button.innerText, button.getAttribute("aria-label"), button.title].filter(Boolean).join(" ").trim();
				return /retry|try again|重試|重新傳送/i.test(labels);
			});
			if (retry && !retry.disabled) {
				retriedAccountChange = true;
				diagnostic(job, "account-changed-retry");
				retry.click();
				previous = "";
				stableReads = 0;
				continue;
			}
		}
		const messages = assistantMessages();
		const latest = messages.at(-1);
		const answer = (latest?.innerText || "").replace(/^(ChatGPT said|ChatGPT 說|ChatGPT说)\s*[:：]?\s*/i, "").trim();
		const generating = !!document.querySelector('button[data-testid="stop-button"], button[aria-label*="Stop streaming"], button[aria-label*="停止產生"], button[aria-label*="停止生成"]');
		const newReply = messages.length > previousCount && answer.length >= 30;
		if (newReply && !generating && answer === previous) stableReads++;
		else stableReads = 0;
		if (stableReads >= 5) {
			diagnostic(job, `answer-captured:length=${answer.length};sources=${collectSources(latest).length}`);
			await report("result", { jobId: job.id, query: job.query, answer, sources: collectSources(latest) });
			await chrome.storage.session.remove("asuisuiActiveJob");
			return;
		}
		previous = answer;
	}
	const pageText = document.body?.innerText || "";
	const notice = pageText.split(/\n+/).map(line => line.trim()).find(line =>
		/account|changed|error|failed|重試|變更|錯誤|失敗|重新傳送/i.test(line)
	)?.slice(0, 180);
	diagnostic(job, `answer-timeout;notice=${notice || "none"}`);
	await report("result", {
		jobId: job.id,
		query: job.query,
		error: notice ? `ChatGPT 未回覆；頁面提示：${notice}` : "ChatGPT 未在 150 秒內產生穩定回答。"
	});
	await chrome.storage.session.remove("asuisuiActiveJob");
}

async function report(type, payload) {
	try { return await chrome.runtime.sendMessage({ type, payload }); }
	catch { return null; }
}

function diagnostic(job, stage) {
	void report("diagnostic", { jobId: job.id, stage });
}

async function runJob(job) {
	if (active || !job?.id || !job?.query || job.id === seenJobId) return;
	active = true;
	seenJobId = job.id;
	try {
		diagnostic(job, "job-received");
		const previousCount = assistantMessages().length;
		diagnostic(job, "waiting-for-composer");
		const composer = await waitForComposer();
		diagnostic(job, "composer-found");
		const promptText = `請搜尋網路並根據搜尋結果回答以下問題，附上可點擊的來源引用。\n\n${job.query}`;
		await chrome.storage.session.set({ asuisuiActiveJob: { ...job, baselineAssistantCount: previousCount, promptText, phase: "submitting" } });
		diagnostic(job, "active-job-stored");
		await submitPrompt(composer, promptText, job);
		diagnostic(job, "prompt-confirmed");
		await report("submitted", { jobId: job.id });
		diagnostic(job, "submitted-reported");
		await waitForAnswer(job, previousCount);
	} catch (error) {
		console.error("Asuisui ChatGPT web automation failed", error);
		diagnostic(job, `failed:${String(error).slice(0, 100)}`);
		await report("result", { jobId: job.id, query: job.query, error: String(error) });
		await chrome.storage.session.remove("asuisuiActiveJob");
	} finally { active = false; }
}

async function resumeSavedJob() {
	const saved = (await chrome.storage.session.get("asuisuiActiveJob")).asuisuiActiveJob;
	if (!saved || saved.provider !== "chatgpt" || saved.phase !== "submitting" || active || seenJobId === saved.id) return;
	const owned = await chrome.runtime.sendMessage({ type: "is-owned-job", payload: { jobId: saved.id } });
	if (!owned?.ok || !await ensureCurrentBridgeVersion(saved)) return;
	active = true;
	seenJobId = saved.id;
	try {
		diagnostic(saved, "resuming-submission-check");
		await confirmSubmission(findComposer(), saved.promptText, saved, 0);
		await report("submitted", { jobId: saved.id });
		await waitForAnswer(saved, saved.baselineAssistantCount || 0);
	} catch (error) {
		diagnostic(saved, `failed:${String(error).slice(0, 100)}`);
		await report("result", { jobId: saved.id, query: saved.query, error: String(error) });
		await chrome.storage.session.remove("asuisuiActiveJob");
	} finally { active = false; }
}

async function pollForJobs() {
	if (active) return;
	try {
		await resumeSavedJob();
		if (active) return;
		const jobId = location.hash.match(/^#asuisui-job=([^&]+)/)?.[1];
		if (!jobId) return;
		const job = await chrome.runtime.sendMessage({ type: "poll-job", payload: { jobId: decodeURIComponent(jobId) } });
		if (job?.id && job.provider === "chatgpt" && await ensureCurrentBridgeVersion(job)) await runJob(job);
	} catch { }
}

setInterval(pollForJobs, 800);
pollForJobs();
