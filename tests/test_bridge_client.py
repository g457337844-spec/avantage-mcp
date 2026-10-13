"""Exercise IPC against real disposable processes, never simulated XPS fitting."""

import asyncio
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import sys
import time

import pytest

from avantage_mcp.bridge_client import BridgeClient, BridgeError


WORKER = r'''
import json, os, sys, time
for line in sys.stdin.buffer:
    request = json.loads(line)
    op, args = request["operation"], request["arguments"]
    response = {"id": request["id"], "ok": True, "result": args}
    if op == "open_spectrum":
        response["result"] = {"session_id": "s-" + str(os.getpid())}
    elif op == "sleep":
        time.sleep(30)
    elif op == "crash":
        sys.stderr.write("deliberate worker failure\n")
        sys.stderr.flush()
        os._exit(17)
    elif op == "wrong_id":
        response["id"] = "wrong-id"
    elif op == "bad_json":
        sys.stdout.write("not json\n")
        sys.stdout.flush()
        continue
    elif op == "large":
        response["result"] = {"data": "X" * 5000}
    elif op == "stderr":
        sys.stderr.write("X" * 900000)
        sys.stderr.flush()
    elif op == "native_error":
        response = {"id": request["id"], "ok": False,
                    "error": {"code": "NO_SUCH_FILE", "message": "File is missing.", "details": {"path": args["path"]}}}
    elif op == "bad_error":
        response = {"id": request["id"], "ok": False, "error": "bad"}
    elif op == "missing_result":
        response.pop("result")
    elif op == "nan":
        response["result"] = float("nan")
    encoded = json.dumps(response, ensure_ascii=False).encode("utf-8")
    sys.stdout.buffer.write(encoded + (b"" if op == "partial" else b"\n"))
    sys.stdout.buffer.flush()
    if op == "partial":
        break
'''


@pytest.fixture
def worker(tmp_path: Path) -> list[str]:
    script = tmp_path / "protocol_worker.py"
    script.write_text(WORKER, encoding="utf-8")
    return [getattr(sys, "_base_executable", sys.executable), "-I", "-S", "-u", str(script)]


def test_unicode_framing_and_serialized_concurrent_calls(worker: list[str]) -> None:
    with BridgeClient(command=worker, timeout=15) as bridge:
        values = [{"path": rf"E:\实验数据\样品-{i}.VGD", "index": i} for i in range(20)]
        with ThreadPoolExecutor(max_workers=8) as executor:
            results = list(executor.map(lambda value: bridge.call("echo", value), values))
        assert results == values
        assert bridge.pid is not None
    assert bridge.pid is None
    with pytest.raises(BridgeError, match="CLIENT_CLOSED"):
        bridge.call("echo")


def test_native_error_keeps_worker_usable(worker: list[str]) -> None:
    with BridgeClient(command=worker, timeout=15) as bridge:
        with pytest.raises(BridgeError) as failure:
            bridge.call("native_error", {"path": "missing.vgd"})
        assert failure.value.code == "NO_SUCH_FILE"
        assert failure.value.details == {"path": "missing.vgd"}
        process_id = bridge.pid
        assert bridge.call("echo", {"valid": True}) == {"valid": True}
        assert bridge.pid == process_id


def test_timeout_terminates_owned_worker_and_invalidates_sessions(worker: list[str]) -> None:
    with BridgeClient(command=worker, timeout=15) as bridge:
        opened = bridge.call("open_spectrum", {"path": "sample.vgd"})
        session_id = opened["session_id"]
        bridge.timeout = 0.35
        started = time.monotonic()
        with pytest.raises(BridgeError) as failure:
            bridge.call("sleep", {"session_id": session_id})
        assert failure.value.code == "BRIDGE_TIMEOUT"
        assert time.monotonic() - started < 5
        assert failure.value.details["invalidated_sessions"] == [session_id]
        assert bridge.pid is None
        with pytest.raises(BridgeError, match="SESSION_INVALIDATED"):
            bridge.call("get_spectrum_info", {"session_id": session_id})
        bridge.timeout = 15
        assert bridge.call("echo", {"restarted": True}) == {"restarted": True}


def test_crash_reports_stderr_and_can_restart(worker: list[str]) -> None:
    with BridgeClient(command=worker, timeout=15) as bridge:
        with pytest.raises(BridgeError) as failure:
            bridge.call("crash")
        assert failure.value.code == "BRIDGE_EXITED"
        assert "deliberate worker failure" in failure.value.details["stderr"]
        assert bridge.pid is None
        assert bridge.call("echo", {"restart": "ok"}) == {"restart": "ok"}


def test_shutdown_interrupts_an_inflight_operation(worker: list[str]) -> None:
    with BridgeClient(command=worker, timeout=30) as bridge:
        bridge.call("echo")
        with ThreadPoolExecutor(max_workers=1) as executor:
            pending = executor.submit(bridge.call, "sleep")
            time.sleep(0.1)
            started = time.monotonic()
            bridge.close()
            assert time.monotonic() - started < 3
            assert bridge.pid is None
            with pytest.raises(BridgeError, match="CLIENT_CLOSED"):
                pending.result(timeout=1)


def test_timeout_also_covers_a_worker_that_never_reads_stdin() -> None:
    command = [getattr(sys, "_base_executable", sys.executable), "-I", "-S", "-c", "import time; time.sleep(30)"]
    with BridgeClient(command=command, timeout=0.5) as bridge:
        started = time.monotonic()
        with pytest.raises(BridgeError, match="BRIDGE_TIMEOUT"):
            bridge.call("echo", {"large": "X" * (256 * 1024)})
        assert time.monotonic() - started < 5
        assert bridge.pid is None


@pytest.mark.parametrize("operation", ["wrong_id", "bad_json", "partial", "bad_error", "missing_result", "nan"])
def test_invalid_responses_stop_the_worker(worker: list[str], operation: str) -> None:
    with BridgeClient(command=worker, timeout=15) as bridge:
        with pytest.raises(BridgeError, match="BRIDGE_PROTOCOL_ERROR"):
            bridge.call(operation)
        assert bridge.pid is None


def test_response_limit_and_bounded_stderr(worker: list[str]) -> None:
    with BridgeClient(command=worker, timeout=15, max_response_bytes=1024) as bridge:
        assert bridge.call("stderr") == {}
        assert len(bridge.stderr_tail) <= 8192
        assert len(bridge._stderr) <= 128
        with pytest.raises(BridgeError, match="BRIDGE_PROTOCOL_ERROR"):
            bridge.call("large")
        assert bridge.pid is None


def test_nonfinite_request_rejected_without_spawning(worker: list[str]) -> None:
    with BridgeClient(command=worker, timeout=15) as bridge:
        with pytest.raises(BridgeError, match="INVALID_ARGUMENTS"):
            bridge.call("echo", {"value": float("nan")})
        assert bridge.pid is None


def test_missing_executable_has_actionable_error(tmp_path: Path) -> None:
    with BridgeClient(executable=tmp_path / "missing.exe") as bridge:
        with pytest.raises(BridgeError) as failure:
            bridge.call("status")
        assert failure.value.code == "BRIDGE_START_FAILED"
        assert "AVANTAGE_BRIDGE_PATH" in failure.value.message


@pytest.mark.parametrize("timeout", [0, -1, float("nan"), float("inf")])
def test_timeout_must_be_finite_positive(timeout: float) -> None:
    with pytest.raises(ValueError, match="positive finite"):
        BridgeClient(timeout=timeout)


def test_mcp_schema_rejects_bad_arguments_before_native_launch(worker: list[str]) -> None:
    from mcp.server.fastmcp.exceptions import ToolError
    from avantage_mcp.server import create_server

    async def scenario() -> None:
        with BridgeClient(command=worker, timeout=15) as bridge:
            server = create_server(bridge)
            tools = {tool.name: tool for tool in await server.list_tools()}
            schema = tools["avantage_get_spectrum_data"].inputSchema
            assert schema["properties"]["limit"]["minimum"] == 1
            assert schema["properties"]["limit"]["maximum"] == 10000
            assert tools["avantage_get_spectrum_info"].annotations.readOnlyHint is True
            assert tools["avantage_save_spectrum"].annotations.destructiveHint is True
            assert bridge.pid is None
            for name, args in [
                ("avantage_open_spectrum", {"path": "relative.vgd"}),
                ("avantage_get_spectrum_data", {"session_id": "s1", "limit": -1}),
                ("avantage_charge_shift", {"session_id": "s1", "delta_ev": float("inf")}),
                ("avantage_add_peak", {"session_id": "s1", "peak": {"label": "C1s", "centre_ev": 285, "height": 10, "fwhm_ev": 0}}),
                ("avantage_update_peak", {"session_id": "s1", "peak_id": "p1", "patch": {}}),
                ("avantage_set_peak_constraint", {"session_id": "s1", "peak_id": "p1", "attribute": "centre", "constraint": {"type": "linked", "value1": 1}}),
            ]:
                with pytest.raises(ToolError):
                    await server.call_tool(name, args)
                assert bridge.pid is None
            result = await server.call_tool("avantage_status", {})
            assert result is not None
            assert bridge.pid is not None

    asyncio.run(scenario())
