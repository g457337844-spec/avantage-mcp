"""Opt-in real MCP rejection checks; no application window is opened or closed."""
import asyncio
import os
from pathlib import Path
import sys
import pytest
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client
from test_native_projects import project_copy
from avantage_mcp.bridge_client import BridgeClient, BridgeError

pytestmark = pytest.mark.skipif(
    os.environ.get("AVANTAGE_LIVE_INTEGRATION") != "1"
    or os.environ.get("AVANTAGE_INTEGRATION") != "1"
    or not os.environ.get("AVANTAGE_VGP_FIXTURE"),
    reason="Requires explicit opt-in, installed Avantage and a supplied private fixture",
)

def test_real_mcp_disabled_live_writes_never_create_windows(project_copy, tmp_path):
    async def run():
        root=Path(__file__).resolve().parents[1]
        params=StdioServerParameters(command=sys.executable,args=["-m","avantage_mcp.server"],cwd=str(root),
            env={**os.environ,"AVANTAGE_DISABLE_LIVE":"1","AVANTAGE_BRIDGE_PATH":os.environ.get("AVANTAGE_BRIDGE_PATH",str(root/"native/AvantageBridge.exe"))})
        async with stdio_client(params) as (read,write):
            async with ClientSession(read,write) as client:
                await client.initialize()
                assert len((await client.list_tools()).tools)==43
                async def good(name,args):
                    result=await client.call_tool("avantage_"+name,args)
                    assert not result.isError,result.content
                    return result.structuredContent
                status=await good("status",{})
                assert status["native_document_lifecycle_verified"] is False
                project=await good("open_processing_project",{"path":str(project_copy)})
                row=next(row for row in project["spectra"] if row["fitted_peak_count"])
                selected=await good("open_project_spectrum",{"project_id":project["project_id"],"spectrum_index":row["spectrum_index"]})
                cases=[
                    ("open_processing_document",{"path":str(project_copy)},"LIVE_OPEN_NOT_VERIFIED"),
                    ("apply_fit_to_document",{"session_id":selected["session_id"],"save_to_original":False},"LIVE_DOCUMENT_BRIDGE_NOT_VERIFIED"),
                    ("save_processing_document",{"document_id":"not-a-live-document","path":str(project_copy)},"LIVE_DOCUMENT_BRIDGE_NOT_VERIFIED"),
                    ("save_processing_document_in_place",{"document_id":"not-a-live-document","path":str(project_copy)},"LIVE_DOCUMENT_BRIDGE_NOT_VERIFIED"),
                ]
                for name,args,code in cases:
                    result=await client.call_tool("avantage_"+name,args)
                    assert result.isError and code in str(result.content),result
                assert not (tmp_path/"must-not-exist.vgp").exists()
    asyncio.run(run())


def test_native_boundary_rejects_live_calls_before_resolving_targets(monkeypatch):
    monkeypatch.setenv("AVANTAGE_DISABLE_LIVE", "1")
    root=Path(__file__).resolve().parents[1]
    executable=os.environ.get("AVANTAGE_BRIDGE_PATH",str(root/"native/AvantageBridge.exe"))
    with BridgeClient(executable,timeout=20) as client:
        assert client.call("status")["native_document_lifecycle_verified"] is False
        cases=[
            ("list_processing_documents",{},"LIVE_CONNECTION_DISABLED"),
            ("apply_fit_to_document",{"session_id":"invalid"},"LIVE_APPLY_DISABLED"),
            ("save_processing_document_existing",{"document_id":"invalid"},"LIVE_SAVE_DISABLED"),
        ]
        for operation,arguments,code in cases:
            with pytest.raises(BridgeError,match=code):
                client.call(operation,arguments)
