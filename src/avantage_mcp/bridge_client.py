"""Serialized, bounded JSON-lines IPC to a private native STA worker.

Only this server's worker is terminated on protocol failure or timeout. The
Avantage application's process is never killed. A restarted worker cannot reuse
in-memory sessions, projects or document proxies, so their stale ids are rejected.
"""

from __future__ import annotations

import json
import math
import os
from pathlib import Path
import queue
import subprocess
import threading
import time
from collections import deque
from typing import Any, Sequence
from uuid import uuid4


class BridgeError(RuntimeError):
    """A native operation or transport failed without a usable result."""

    def __init__(self, code: str, message: str, details: Any = None):
        super().__init__(f"{code}: {message}")
        self.code = code
        self.message = message
        self.details = details


def default_bridge_path() -> Path:
    """Resolve the editable source deployment or an explicit native executable."""
    override = os.environ.get("AVANTAGE_BRIDGE_PATH")
    if override:
        return Path(override).expanduser().resolve()
    return Path(__file__).resolve().parents[2] / "native" / "AvantageBridge.exe"


class BridgeClient:
    """Own one subprocess, serialize calls, and isolate process failures.

    ``command`` is an internal injection point for IPC tests. Normal callers
    supply only an executable path; no shell or command-string parsing is used.
    """

    def __init__(
        self,
        executable: str | Path | None = None,
        *,
        timeout: float = 120.0,
        command: Sequence[str] | None = None,
        max_response_bytes: int = 16 * 1024 * 1024,
    ) -> None:
        if not math.isfinite(timeout) or timeout <= 0:
            raise ValueError("timeout must be a positive finite number")
        if max_response_bytes < 128:
            raise ValueError("max_response_bytes must be at least 128")
        self.executable = Path(executable) if executable else default_bridge_path()
        self.command = list(command) if command is not None else [str(self.executable)]
        if not self.command:
            raise ValueError("command cannot be empty")
        self.timeout = timeout
        self.max_response_bytes = max_response_bytes
        self._lock = threading.RLock()
        self._process: subprocess.Popen[bytes] | None = None
        self._responses: queue.Queue[tuple[str, Any]] = queue.Queue(maxsize=4)
        self._stderr: deque[str] = deque(maxlen=128)
        self._threads: list[threading.Thread] = []
        self._writer_thread: threading.Thread | None = None
        self._write_results: queue.Queue[Exception | None] | None = None
        self._known_sessions: set[str] = set()
        self._invalidated_sessions: set[str] = set()
        self._known_projects: set[str] = set()
        self._invalidated_projects: set[str] = set()
        self._known_documents: set[str] = set()
        self._invalidated_documents: set[str] = set()
        self._closed = False

    @property
    def stderr_tail(self) -> str:
        return "".join(list(self._stderr))[-8192:]

    @property
    def pid(self) -> int | None:
        with self._lock:
            return self._process.pid if self._process else None

    def _start(self) -> None:
        if self._closed:
            raise BridgeError("CLIENT_CLOSED", "The bridge client has been shut down.")
        if self._process is not None:
            if self._process.poll() is None:
                return
            self._fail("BRIDGE_EXITED", "Native worker exited; reopen affected spectra.")
        self._responses = queue.Queue(maxsize=4)
        self._stderr.clear()
        try:
            process = subprocess.Popen(
                self.command,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                bufsize=-1,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0) if os.name == "nt" else 0,
            )
        except OSError as exc:
            raise BridgeError(
                "BRIDGE_START_FAILED",
                "Cannot launch the native bridge. Build it or set AVANTAGE_BRIDGE_PATH.",
                {"executable": self.command[0], "reason": str(exc)},
            ) from exc
        self._process = process
        if self._closed:
            self._stop()
            raise BridgeError("CLIENT_CLOSED", "The server shut down while starting the native worker.")
        self._threads = [
            threading.Thread(target=self._read_stdout, args=(process, self._responses), daemon=True),
            threading.Thread(target=self._read_stderr, args=(process,), daemon=True),
        ]
        for thread in self._threads:
            thread.start()

    def _read_stdout(
        self, process: subprocess.Popen[bytes], responses: queue.Queue[tuple[str, Any]]
    ) -> None:
        assert process.stdout is not None
        try:
            while True:
                line = process.stdout.readline(self.max_response_bytes + 1)
                if not line:
                    event = ("eof", None)
                elif len(line) > self.max_response_bytes:
                    event = ("error", "Native response exceeded the size limit.")
                elif not line.endswith(b"\n"):
                    event = ("error", "Native response ended without a newline.")
                else:
                    event = ("line", line)
                try:
                    responses.put(event, timeout=0.2)
                except queue.Full:
                    # A conforming bridge writes exactly one response per call.
                    process.kill()
                    return
                if event[0] != "line":
                    return
        except (OSError, ValueError) as exc:
            try:
                responses.put(("error", str(exc)), timeout=0.2)
            except queue.Full:
                pass

    def _read_stderr(self, process: subprocess.Popen[bytes]) -> None:
        assert process.stderr is not None
        try:
            while chunk := process.stderr.read1(4096):
                self._stderr.append(chunk.decode("utf-8", errors="replace"))
        except (OSError, ValueError):
            pass

    def _stop(self, *, graceful: bool = False) -> list[str]:
        invalidated = sorted(self._known_sessions)
        self._invalidated_sessions.update(self._known_sessions)
        self._known_sessions.clear()
        self._invalidated_projects.update(self._known_projects)
        self._known_projects.clear()
        self._invalidated_documents.update(self._known_documents)
        self._known_documents.clear()
        process, self._process = self._process, None
        if process is not None:
            if graceful and process.stdin:
                try:
                    process.stdin.close()
                except OSError:
                    pass
            if process.poll() is None:
                try:
                    if graceful:
                        process.wait(timeout=1.0)
                    else:
                        process.terminate()
                        process.wait(timeout=2.0)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=2.0)
            for stream in (process.stdin, process.stdout, process.stderr):
                if stream:
                    try:
                        stream.close()
                    except OSError:
                        pass
            for thread in self._threads:
                thread.join(timeout=0.3)
            if self._writer_thread:
                self._writer_thread.join(timeout=0.3)
        self._threads = []
        self._writer_thread = None
        self._write_results = None
        return invalidated

    def _fail(self, code: str, message: str) -> None:
        projects = sorted(self._known_projects)
        documents = sorted(self._known_documents)
        sessions = self._stop()
        raise BridgeError(code, message, {
            "invalidated_sessions": sessions, "invalidated_projects": projects,
            "invalidated_documents": documents, "stderr": self.stderr_tail,
        })

    def call(self, operation: str, arguments: dict[str, Any] | None = None) -> Any:
        """Perform exactly one native operation; timeout includes worker response.

        Requests are never retried automatically because mutations may have
        completed before their response was lost.
        """
        with self._lock:
            if self._closed:
                raise BridgeError("CLIENT_CLOSED", "The bridge client has been shut down.")
            args = arguments or {}
            session_id = args.get("session_id")
            if session_id in self._invalidated_sessions:
                raise BridgeError(
                    "SESSION_INVALIDATED", "The native worker restarted. Reopen this spectrum.",
                    {"session_id": session_id},
                )
            project_id = args.get("project_id")
            if project_id in self._invalidated_projects:
                raise BridgeError(
                    "PROJECT_INVALIDATED", "The native worker restarted. Reopen this VGP project.",
                    {"project_id": project_id},
                )
            document_id = args.get("document_id")
            if document_id in self._invalidated_documents:
                raise BridgeError(
                    "DOCUMENT_INVALIDATED", "The native worker restarted. Relist Avantage documents and reattach the spectrum.",
                    {"document_id": document_id},
                )
            request_id = uuid4().hex
            try:
                payload = json.dumps(
                    {"id": request_id, "operation": operation, "arguments": args},
                    ensure_ascii=False, allow_nan=False, separators=(",", ":"),
                ).encode("utf-8") + b"\n"
            except (TypeError, ValueError) as exc:
                raise BridgeError("INVALID_ARGUMENTS", str(exc)) from exc
            self._start()
            assert self._process is not None and self._process.stdin is not None
            input_stream = self._process.stdin
            written: queue.Queue[Exception | None] = queue.Queue(maxsize=1)
            self._write_results = written
            if self._closed:
                self._fail("CLIENT_CLOSED", "The server shut down before sending the native request.")
            deadline = time.monotonic() + self.timeout

            def send_request() -> None:
                result: Exception | None = None
                try:
                    input_stream.write(payload)
                    input_stream.flush()
                except (OSError, ValueError) as exc:
                    result = exc
                try:
                    written.put_nowait(result)
                except queue.Full:
                    pass

            self._writer_thread = threading.Thread(target=send_request, daemon=True)
            self._writer_thread.start()
            try:
                write_error = written.get(timeout=self.timeout)
            except queue.Empty:
                self._fail("BRIDGE_TIMEOUT", "Native worker did not accept the request before timeout. Its outcome may be unknown.")
            if write_error is not None:
                if self._closed:
                    self._fail("CLIENT_CLOSED", "The server shut down during the native request. Its outcome may be unknown.")
                self._fail("BRIDGE_IO_ERROR", f"Cannot write to the native worker: {write_error}")
            self._writer_thread.join(timeout=0.1)
            self._writer_thread = None
            self._write_results = None
            try:
                kind, value = self._responses.get(timeout=max(0.0, deadline - time.monotonic()))
            except queue.Empty:
                self._fail(
                    "BRIDGE_TIMEOUT",
                    f"Native operation {operation!r} exceeded {self.timeout:g}s. Its outcome is unknown; inspect any destination file before retrying.",
                )
            if kind == "eof":
                self._fail("BRIDGE_EXITED", "Native worker exited before returning a complete result.")
            if kind == "closed":
                self._fail("CLIENT_CLOSED", "The server shut down during the native operation. Its outcome may be unknown.")
            if kind == "error":
                self._fail("BRIDGE_PROTOCOL_ERROR", str(value))
            try:
                def reject_constant(token: str) -> None:
                    raise ValueError(f"Non-finite JSON constant: {token}")
                response = json.loads(value.decode("utf-8"), parse_constant=reject_constant)
            except (ValueError, UnicodeError) as exc:
                self._fail("BRIDGE_PROTOCOL_ERROR", f"Invalid JSON response: {exc}")
            if not isinstance(response, dict) or response.get("id") != request_id:
                self._fail("BRIDGE_PROTOCOL_ERROR", "Native response has an unexpected request id.")
            if not isinstance(response.get("ok"), bool):
                self._fail("BRIDGE_PROTOCOL_ERROR", "Native response must contain a Boolean 'ok'.")
            if not response["ok"]:
                error = response.get("error")
                if not isinstance(error, dict) or not isinstance(error.get("code"), str) or not isinstance(error.get("message"), str):
                    self._fail("BRIDGE_PROTOCOL_ERROR", "Native error response is malformed.")
                raise BridgeError(error["code"], error["message"], error.get("details"))
            if "result" not in response:
                self._fail("BRIDGE_PROTOCOL_ERROR", "Native response is missing its result.")
            result = response["result"]
            if operation in {"open_spectrum", "open_project_spectrum", "attach_document_spectrum"} and isinstance(result, dict):
                opened_id = result.get("session_id")
                if isinstance(opened_id, str):
                    self._known_sessions.add(opened_id)
                    self._invalidated_sessions.discard(opened_id)
            if operation == "close_session" and isinstance(session_id, str):
                self._known_sessions.discard(session_id)
            if operation == "open_processing_project" and isinstance(result, dict):
                opened_id = result.get("project_id")
                if isinstance(opened_id, str):
                    self._known_projects.add(opened_id)
                    self._invalidated_projects.discard(opened_id)
            if operation == "close_processing_project" and isinstance(project_id, str):
                self._known_projects.discard(project_id)
            if operation in {
                "list_processing_documents", "open_processing_document", "list_document_spectra",
                "save_processing_document",
            } and isinstance(result, dict):
                documents = result.get("documents", [])
                if isinstance(result.get("document"), dict):
                    documents = [result["document"]]
                if isinstance(documents, list):
                    for document in documents:
                        opened_id = document.get("document_id") if isinstance(document, dict) else None
                        if isinstance(opened_id, str):
                            self._known_documents.add(opened_id)
                            self._invalidated_documents.discard(opened_id)
            return result

    def close(self) -> None:
        """Release only the owned worker and its in-memory native objects."""
        # Wake an in-flight request before taking the serialization lock so a
        # shutdown does not wait for the entire scientific-operation timeout.
        self._closed = True
        pending_write = self._write_results
        if pending_write is not None:
            try:
                pending_write.put_nowait(BridgeError("CLIENT_CLOSED", "Server shutdown requested."))
            except queue.Full:
                pass
        try:
            self._responses.put_nowait(("closed", None))
        except queue.Full:
            pass
        with self._lock:
            self._stop(graceful=True)

    def __enter__(self) -> "BridgeClient":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()
