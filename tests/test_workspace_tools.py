"""A missing or malformed activity snapshot must never trigger pruning."""

import asyncio

import pytest
from mcp.server.fastmcp.exceptions import ToolError

from avantage_mcp import server
from avantage_mcp.bridge_client import BridgeError


@pytest.mark.parametrize("snapshot", [None, {}, {"sessions": None}, {"sessions": [None]},
                                     {"sessions": [{}]}, {"sessions": [{"working_path": ""}]}])
def test_invalid_activity_response_stops_cleanup(snapshot, monkeypatch):
    class Bridge:
        def call(self, operation, arguments):
            assert operation == "list_sessions"
            return snapshot

    def unexpected(**kwargs):
        pytest.fail("Cleanup must not run without a complete activity response")

    monkeypatch.setattr(server.workspace, "prune_workspace", unexpected)
    with pytest.raises(ToolError, match="WORKSPACE_ACTIVITY_UNKNOWN"):
        asyncio.run(server.create_server(Bridge()).call_tool("avantage_prune_workspace", {"dry_run": False}))


def test_worker_failure_stops_cleanup(monkeypatch):
    class Bridge:
        def call(self, operation, arguments):
            raise BridgeError("BRIDGE_TIMEOUT", "worker unavailable")

    def unexpected(**kwargs):
        pytest.fail("Cleanup must not run after bridge failure")

    monkeypatch.setattr(server.workspace, "prune_workspace", unexpected)
    with pytest.raises(ToolError, match="BRIDGE_TIMEOUT"):
        asyncio.run(server.create_server(Bridge()).call_tool("avantage_prune_workspace", {"dry_run": False}))
