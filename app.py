"""Local first command line prototype for AsuisuiLLM."""

import argparse
import os
import sqlite3
import sys
import urllib.error
import urllib.request
import json
from datetime import datetime, timezone
from pathlib import Path


DATA_DIR = Path(os.environ.get("ASUISUI_DATA_DIR", Path(__file__).resolve().parent / "data"))
DB_PATH = DATA_DIR / "assistant.sqlite3"
OLLAMA_URL = os.environ.get("OLLAMA_URL", "http://127.0.0.1:11434")
MODEL = os.environ.get("ASUISUI_MODEL", "qwen3:8b-q4_K_M")


def database():
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    connection = sqlite3.connect(DB_PATH)
    connection.execute("CREATE TABLE IF NOT EXISTS messages (id INTEGER PRIMARY KEY, created_at TEXT NOT NULL, role TEXT NOT NULL, content TEXT NOT NULL)")
    connection.execute("CREATE TABLE IF NOT EXISTS tasks (id INTEGER PRIMARY KEY, created_at TEXT NOT NULL, title TEXT NOT NULL, due_at TEXT, done INTEGER NOT NULL DEFAULT 0)")
    connection.commit()
    return connection


def now():
    return datetime.now(timezone.utc).isoformat()


def ollama_chat(messages):
    payload = json.dumps({"model": MODEL, "messages": messages, "stream": False}).encode("utf-8")
    request = urllib.request.Request(
        OLLAMA_URL.rstrip("/") + "/api/chat",
        data=payload,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=300) as response:
            result = json.load(response)
    except (urllib.error.URLError, TimeoutError) as error:
        raise RuntimeError(f"無法連接本機 Ollama：{error}") from error
    return result["message"]["content"]


def chat(connection, prompt):
    history = connection.execute("SELECT role, content FROM messages ORDER BY id DESC LIMIT 20").fetchall()
    messages = [{"role": "system", "content": "你是使用者的本機個人助理。以繁體中文回答。不要聲稱已執行未執行的操作。"}]
    messages.extend({"role": role, "content": content} for role, content in reversed(history))
    messages.append({"role": "user", "content": prompt})
    answer = ollama_chat(messages)
    connection.executemany("INSERT INTO messages (created_at, role, content) VALUES (?, ?, ?)", [(now(), "user", prompt), (now(), "assistant", answer)])
    connection.commit()
    print(answer)


def main():
    parser = argparse.ArgumentParser(description="AsuisuiLLM 本機助理原型")
    commands = parser.add_subparsers(dest="command", required=True)
    ask = commands.add_parser("chat", help="使用本機 Ollama 模型聊天")
    ask.add_argument("prompt", nargs="+")
    search = commands.add_parser("search", help="搜尋已儲存的對話")
    search.add_argument("term")
    add = commands.add_parser("task-add", help="新增工作")
    add.add_argument("title")
    add.add_argument("--due", help="到期日期，例如 2026-11-30")
    commands.add_parser("task-list", help="列出工作")
    done = commands.add_parser("task-done", help="完成工作")
    done.add_argument("id", type=int)
    commands.add_parser("status", help="顯示本機設定")
    args = parser.parse_args()
    with database() as connection:
        if args.command == "chat":
            chat(connection, " ".join(args.prompt))
        elif args.command == "search":
            rows = connection.execute("SELECT created_at, role, content FROM messages WHERE content LIKE ? ORDER BY id DESC LIMIT 20", ("%" + args.term + "%",)).fetchall()
            for created, role, content in rows:
                print(f"{created} [{role}] {content[:300]}")
        elif args.command == "task-add":
            connection.execute("INSERT INTO tasks (created_at, title, due_at) VALUES (?, ?, ?)", (now(), args.title, args.due))
            connection.commit()
            print("已新增工作")
        elif args.command == "task-list":
            for task_id, title, due, completed in connection.execute("SELECT id, title, due_at, done FROM tasks ORDER BY done, id"):
                print(f"{task_id}. {'✓' if completed else '○'} {title}" + (f"（{due}）" if due else ""))
        elif args.command == "task-done":
            connection.execute("UPDATE tasks SET done = 1 WHERE id = ?", (args.id,))
            connection.commit()
            print("已更新工作")
        else:
            print(f"模型：{MODEL}\nOllama：{OLLAMA_URL}\n資料庫：{DB_PATH}")


if __name__ == "__main__":
    try:
        main()
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
