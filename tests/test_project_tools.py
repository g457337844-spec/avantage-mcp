"""Public project contracts and live capability protection; no Avantage application calls."""

import asyncio
from contextlib import nullcontext

import pytest
from mcp.server.fastmcp.exceptions import ToolError

from avantage_mcp.server import create_server


class RecordingBridge:
    def __init__(self, *, live_verified=False, project_save_verified=False):
        self.live_verified = live_verified
        self.project_save_verified = project_save_verified
        self.calls = []

    def exclusive(self):
        return nullcontext()

    def call(self, operation, arguments=None):
        arguments = arguments or {}
        self.calls.append((operation, arguments))
        if operation == "status":
            return {
                "native_live_document_bridge_verified": self.live_verified,
                "native_project_save_verified": self.project_save_verified,
            }
        return {"operation": operation, "arguments": arguments}

    def close(self):
        pass


def test_project_indices_and_ids_are_validated_before_bridge_call():
    async def scenario():
        bridge = RecordingBridge()
        server = create_server(bridge)
        tools = {tool.name: tool for tool in await server.list_tools()}
        index = tools["avantage_open_project_spectrum"].inputSchema["properties"]["spectrum_index"]
        assert index["minimum"] == 0
        assert index["maximum"] == 9999
        for name, args in [
            ("avantage_open_processing_project", {"path": "relative.vgp"}),
            ("avantage_list_project_spectra", {"project_id": ""}),
            ("avantage_open_project_spectrum", {"project_id": "p1", "spectrum_index": -1}),
            ("avantage_attach_document_spectrum", {"document_id": "d1", "spectrum_index": 10000}),
            ("avantage_list_document_spectra", {"document_id": ""}),
            ("avantage_save_project_model", {"session_id": "s1", "path": "relative.vgp"}),
        ]:
            with pytest.raises(ToolError):
                await server.call_tool(name, args)
        assert bridge.calls == []

    asyncio.run(scenario())


def test_project_save_requires_verified_capability_before_any_write():
    async def scenario():
        bridge = RecordingBridge()
        with pytest.raises(ToolError, match="PROJECT_SAVE_NOT_VERIFIED"):
            await create_server(bridge).call_tool(
                "avantage_save_project_model", {"session_id": "s1", "path": r"D:\SyntheticFixtures\结果.vgp"},
            )
        assert bridge.calls == [("status", {})]

    asyncio.run(scenario())


def test_verified_project_save_forwards_session_and_unicode_destination():
    async def scenario():
        bridge = RecordingBridge(project_save_verified=True)
        arguments = {"session_id": "s1", "path": r"D:\SyntheticFixtures\结果.vgp"}
        await create_server(bridge).call_tool("avantage_save_project_model", arguments)
        assert bridge.calls == [("status", {}), ("save_project_model", arguments)]

    asyncio.run(scenario())


@pytest.mark.parametrize("name,args,operation", [
    ("avantage_open_processing_project", {"path": r"D:\SyntheticFixtures\C.vgp"}, "open_processing_project"),
    ("avantage_list_project_spectra", {"project_id": "p1"}, "list_project_spectra"),
    ("avantage_open_project_spectrum", {"project_id": "p1", "spectrum_index": 9}, "open_project_spectrum"),
    ("avantage_close_processing_project", {"project_id": "p1"}, "close_processing_project"),
])
def test_project_tools_forward_ids_and_unicode_paths(name, args, operation):
    async def scenario():
        bridge = RecordingBridge()
        await create_server(bridge).call_tool(name, args)
        assert bridge.calls == [(operation, args)]

    asyncio.run(scenario())


LIVE_CASES = [
    ("avantage_list_processing_documents", {}, "list_processing_documents"),
    ("avantage_list_document_spectra", {"document_id": "d1"}, "list_document_spectra"),
    ("avantage_attach_document_spectrum", {"document_id": "d1", "spectrum_index": 9}, "attach_document_spectrum"),
    ("avantage_apply_fit_to_document", {"session_id": "s1", "save_to_original": False}, "apply_fit_to_document"),
]


@pytest.mark.parametrize('live_verified', [False, True])
def test_automatic_open_is_disabled_even_when_live_reads_are_verified(live_verified):
    async def scenario():
        bridge = RecordingBridge(live_verified=live_verified)
        with pytest.raises(ToolError, match='LIVE_OPEN_NOT_VERIFIED'):
            await create_server(bridge).call_tool('avantage_open_processing_document', {'path': r'D:\SyntheticFixtures\2.vgp'})
        assert bridge.calls == []
    asyncio.run(scenario())


@pytest.mark.parametrize("name,args,operation", LIVE_CASES)
def test_unverified_live_routes_never_reach_application(name, args, operation):
    async def scenario():
        bridge = RecordingBridge()
        with pytest.raises(ToolError, match="LIVE_DOCUMENT_BRIDGE_NOT_VERIFIED"):
            await create_server(bridge).call_tool(name, args)
        assert bridge.calls == [("status", {})]

    asyncio.run(scenario())


@pytest.mark.parametrize("name,args,operation", LIVE_CASES)
def test_verified_live_routes_forward_after_capability_check(name, args, operation):
    async def scenario():
        bridge = RecordingBridge(live_verified=True)
        await create_server(bridge).call_tool(name, args)
        expected = {k:v for k,v in args.items() if k != 'save_to_original'}
        assert bridge.calls == [("status", {}), (operation, expected)]

    asyncio.run(scenario())
