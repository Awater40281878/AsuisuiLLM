# Asuisui AI Web Bridge

This local Chrome extension lets AsuisuiLLM read answers and cited source links from Google AI Mode and ChatGPT web directly from their page DOM. It does not take screenshots, use OCR, or call a paid search API.

## One-time setup in the selected Chrome profile

1. Open Chrome with the profile selected by `/設定google` (for example, GGO).
2. Go to `chrome://extensions` and turn on **Developer mode**.
3. Choose **Load unpacked** and select this `google-ai-mode-extension` folder.
4. Keep the extension enabled in that profile.

The extension is limited to Google pages and the AsuisuiLLM loopback bridge on `127.0.0.1:43829`. AsuisuiLLM starts that local bridge only while the app is running. The bridge accepts one active search result at a time.

## Search flow

AsuisuiLLM opens Google AI Mode or `https://chatgpt.com/` in a new, minimized window of the explicitly selected Chrome profile and publishes the current query on its loopback bridge. It identifies the new window by taking a before-launch snapshot and only minimizes a newly created window whose title matches the selected provider; it never chooses the current foreground window. The matching content script submits the query, waits for the answer, and returns visible citation URLs. The extension records this job's tab ID and job ID, then closes only that owned tab after the desktop bridge accepts a successful completed answer. A later provider job never closes an earlier window; incomplete and failed pages remain open for inspection. It does not use keyboard shortcuts, restore a window, or close ordinary tabs. If it cannot identify the new search window, AsuisuiLLM stops without manipulating another window.

Use `/google query` for Google AI Mode and `/chatgpt query` for ChatGPT web search. ChatGPT is asked to use its web search and provide citations. AsuisuiLLM waits for the completed answer, then reads every returned citation URL before using the answer; if none are returned, it tries the next configured provider.

## Safe source reading

Citation pages are fetched with a stateless HTTP reader. It accepts public HTTP/HTTPS hosts on standard ports only, resolves DNS and connects to a validated public IP, checks each redirect, blocks HTTPS-to-HTTP downgrades, and limits redirects and response size. It does not run page JavaScript, submit forms, load user cookies, or open untrusted pages in the user's browser. It records the redirect path, TLS transport, selected security headers, external script hosts, and form destinations without interacting with them. Every returned URL gets a success or failure record; a table cannot pass its audit while any supplied source URL remains unread. JavaScript-only pages are reported as unreadable rather than opened actively. No URLs are submitted to public reputation or sandbox services.

The app compares the packaged extension version with the version loaded in the selected profile when starting a search. If the loaded version is older, the extension asks Chrome to reload itself, then AsuisuiLLM reopens the same search in a fresh minimized window. This uses `chrome.runtime.reload()` and does not request Chrome's broad extension-management permission. After installing this auto-reload version, future project updates no longer require a manual reload. The first installation of this feature still needs one reload from `chrome://extensions` so the currently loaded extension receives the version-check code.
