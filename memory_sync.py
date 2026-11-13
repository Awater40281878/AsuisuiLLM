"""Watch AnythingLLM's local chat database and sync durable memories to Git."""

from __future__ import annotations

import json
import os
import re
import shutil
import sqlite3
import subprocess
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path


ROOT = Path(__file__).resolve().parent
DATA = ROOT / "data"
DB = Path(os.environ.get(
    "ANYTHINGLLM_DB",
    Path(os.environ.get("APPDATA", Path.home() / "AppData" / "Roaming"))
    / "anythingllm-desktop" / "storage" / "anythingllm.db",
))
OLLAMA_URL = os.environ.get("OLLAMA_URL", "http://127.0.0.1:11434")
MODEL = os.environ.get("ASUISUI_MEMORY_MODEL", "qwen3:8b-q4_K_M")
WORKSPACE_SLUG = os.environ.get("ASUISUI_WORKSPACE", "asuisui-ai")
IDLE_SECONDS = int(os.environ.get("ASUISUI_MEMORY_IDLE_SECONDS", "90"))
POLL_SECONDS = int(os.environ.get("ASUISUI_MEMORY_POLL_SECONDS", "20"))
STATE_FILE = DATA / "memory-sync-state.json"
LOG_FILE = DATA / "memory-sync.log"
MEMORY_FILE = ROOT / "memory" / "conversation-memory.md"
SECRET_PATTERN = re.compile(
    r"(?i)(api[_ -]?key|access[_ -]?token|password|passwd|secret|private[_ -]?key|bearer\s+[\w.-]+)"
)
SYSTEM_PROMPT_PREFIX = "你是 Asuisui 的本機女性助理，名字是 Asuisui AI。"
SYSTEM_PROMPT_MARKERS = ("這是明確的角色設定：", "使用者的名字是 Asuisui。")


def resolve_git_executable() -> str:
    configured = os.environ.get("ASUISUI_GIT_EXECUTABLE")
    if configured and Path(configured).is_file():
        return configured
    preferred = Path(r"C:\Program Files\Git\cmd\git.exe")
    if preferred.is_file():
        return str(preferred)
    return shutil.which("git") or "git"


GIT_EXECUTABLE = resolve_git_executable()


def log(message: str) -> None:
    DATA.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().astimezone().isoformat(timespec="seconds")
    with LOG_FILE.open("a", encoding="utf-8") as stream:
        stream.write(f"{stamp} {message}\n")


def read_state() -> dict:
    try:
        return json.loads(STATE_FILE.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {"threads": {}}


def write_state(state: dict) -> None:
    DATA.mkdir(parents=True, exist_ok=True)
    temp = STATE_FILE.with_suffix(".tmp")
    temp.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")
    temp.replace(STATE_FILE)


def db_connection() -> sqlite3.Connection:
    if not DB.exists():
        raise FileNotFoundError(f"AnythingLLM 資料庫不存在：{DB}")
    return sqlite3.connect(f"file:{DB.as_posix()}?mode=ro", uri=True, timeout=5)


def idle_threads() -> list[dict]:
    cutoff_ms = int((time.time() - IDLE_SECONDS) * 1000)
    with db_connection() as conn:
        workspace = conn.execute(
            "SELECT id FROM workspaces WHERE slug = ?", (WORKSPACE_SLUG,)
        ).fetchone()
        if not workspace:
            return []
        rows = conn.execute(
            """SELECT t.id, t.name, MAX(c.id), MAX(c.lastUpdatedAt)
               FROM workspace_threads t
               JOIN workspace_chats c ON c.thread_id = t.id
               WHERE t.workspace_id = ? AND c.include = 1
               GROUP BY t.id, t.name
               HAVING MAX(c.lastUpdatedAt) <= ?""",
            (workspace[0], cutoff_ms),
        ).fetchall()
        threads = []
        for thread_id, name, latest_id, updated in rows:
            chats = conn.execute(
                """SELECT id, prompt, response FROM workspace_chats
                   WHERE thread_id = ? AND include = 1 ORDER BY id""",
                (thread_id,),
            ).fetchall()
            threads.append({
                "id": thread_id,
                "name": name,
                "latest_id": latest_id,
                "updated": updated,
                "chats": chats,
            })
        return threads


def extract_memories(chats: list[tuple]) -> list[str]:
    transcript = []
    for _chat_id, prompt, response in chats[-30:]:
        transcript.append(f"使用者：{prompt}\n助理：{response}")
    text = "\n\n".join(transcript)[-16000:]
    payload = {
        "model": MODEL,
        "stream": False,
        "format": "json",
        "messages": [
            {
                "role": "system",
                "content": (
                    "你是本機記憶整理器。從對話找出值得未來記住的穩定使用者偏好、稱呼、長期目標、"
                    "持續專案或工作方式。只輸出 JSON：{\"memories\":[\"...\"]}。"
                    "每項一個精簡繁體中文句子，最多 5 項。排除閒聊、單次問題、助理自己說的事、推測，"
                    "以及密碼、金鑰、帳號憑證、財務、醫療、精確住址等敏感資料。沒有合適記憶就回傳空陣列。"
                ),
            },
            {"role": "user", "content": text},
        ],
    }
    request = urllib.request.Request(
        OLLAMA_URL.rstrip("/") + "/api/chat",
        data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=240) as response:
        result = json.load(response)
    parsed = json.loads(result["message"]["content"])
    memories = parsed.get("memories", [])
    if not isinstance(memories, list):
        return []
    return [
        item.strip()
        for item in memories[:5]
        if isinstance(item, str) and item.strip() and not SECRET_PATTERN.search(item)
    ]


def is_system_prompt_record(prompt: str) -> bool:
    text = prompt.strip()
    return text.startswith(SYSTEM_PROMPT_PREFIX) and all(
        marker in text for marker in SYSTEM_PROMPT_MARKERS
    )


def update_memory(new_items: list[str], thread_name: str) -> bool:
    if not new_items:
        return False
    MEMORY_FILE.parent.mkdir(parents=True, exist_ok=True)
    existing = MEMORY_FILE.read_text(encoding="utf-8") if MEMORY_FILE.exists() else (
        "# 對話中整理出的長期記憶\n\n此檔由本機記憶同步器更新；僅保存可重用且非敏感的資訊。\n"
    )
    known = {line[2:].strip() for line in existing.splitlines() if line.startswith("- ")}
    additions = [item for item in new_items if item not in known]
    if not additions:
        return False
    stamp = datetime.now().astimezone().strftime("%Y-%m-%d %H:%M %Z")
    section = f"\n## {stamp}｜{thread_name}\n" + "".join(f"- {item}\n" for item in additions)
    MEMORY_FILE.write_text(existing.rstrip() + "\n" + section, encoding="utf-8")
    return True


def git(*args: str, check: bool = True) -> subprocess.CompletedProcess:
    environment = os.environ.copy()
    environment["GIT_TERMINAL_PROMPT"] = "0"
    environment["GCM_INTERACTIVE"] = "false"
    environment["GCM_GUI_PROMPT"] = "false"
    options = {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {}
    return subprocess.run(
        [GIT_EXECUTABLE, *args], cwd=ROOT, text=True, encoding="utf-8",
        errors="replace", capture_output=True, check=check, env=environment, **options,
    )


def commit_local_memory() -> bool:
    relative_memory = str(MEMORY_FILE.relative_to(ROOT))
    git("add", "--", relative_memory)
    changed = git("diff", "--cached", "--quiet", "--", relative_memory, check=False).returncode != 0
    if changed:
        git("commit", "-m", "Update local conversation memory", "--", relative_memory)
    return changed


def finish_pending_local_commit(state: dict) -> None:
    pending = state.get("pending_local_commit")
    if not pending:
        return
    committed = commit_local_memory()
    key = str(pending["thread_id"])
    latest_id = int(pending["latest_id"])
    state.setdefault("threads", {})[key] = max(
        int(state.setdefault("threads", {}).get(key, 0)), latest_id
    )
    state.pop("pending_local_commit", None)
    if committed:
        state["push_pending"] = True
        state["push_retry_after"] = 0
    write_state(state)
    log(f"對話 {key} 的本機記憶已提交；進度已保存。")


def push_pending_memory(state: dict) -> None:
    if not state.get("push_pending") or time.time() < float(state.get("push_retry_after", 0)):
        return
    try:
        git("push", "origin", "main")
    except (OSError, subprocess.CalledProcessError) as error:
        state["push_pending"] = True
        state["push_retry_after"] = time.time() + 300
        write_state(state)
        log(f"本機記憶已保存；遠端推送失敗，5 分鐘後重試：{error}")
        return
    state["push_pending"] = False
    state["push_retry_after"] = 0
    write_state(state)
    log("本機記憶已推送至 origin/main。")


def process_thread(thread: dict, state: dict) -> bool:
    key = str(thread["id"])
    previous_id = int(state.setdefault("threads", {}).get(key, 0))
    new_chats = [row for row in thread["chats"] if row[0] > previous_id]
    if not new_chats:
        return False
    conversation_chats = [row for row in new_chats if not is_system_prompt_record(row[1] or "")]
    skipped = len(new_chats) - len(conversation_chats)
    memories = extract_memories(conversation_chats) if conversation_chats else []
    changed = update_memory(memories, thread["name"] or f"對話 {key}")
    if changed:
        state["pending_local_commit"] = {
            "thread_id": key,
            "latest_id": int(thread["latest_id"]),
        }
        write_state(state)
        finish_pending_local_commit(state)
    else:
        state.setdefault("threads", {})[key] = int(thread["latest_id"])
        write_state(state)
        log(f"對話 {key} 已檢查；沒有新增長期記憶。略過系統提示詞記錄：{skipped}。")
    return changed


def run_once() -> None:
    state = read_state()
    try:
        finish_pending_local_commit(state)
    except (OSError, ValueError, KeyError, sqlite3.Error, subprocess.CalledProcessError) as error:
        log(f"本機記憶提交暫停，稍後重試：{error}")
        return
    for thread in idle_threads():
        try:
            process_thread(thread, state)
        except (OSError, ValueError, KeyError, sqlite3.Error, subprocess.CalledProcessError, urllib.error.URLError) as error:
            log(f"記憶同步暫停，稍後重試：{error}")
    push_pending_memory(state)


def main() -> None:
    watch = "--watch" in sys.argv[1:]
    if watch:
        log(f"開始監看 AnythingLLM 工作區 {WORKSPACE_SLUG}；閒置 {IDLE_SECONDS} 秒後整理記憶。")
        log(f"Git 執行檔：{GIT_EXECUTABLE}")
        while True:
            run_once()
            time.sleep(POLL_SECONDS)
    else:
        run_once()


if __name__ == "__main__":
    main()
