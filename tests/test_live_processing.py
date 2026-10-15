"""Explicitly opt-in, real MCP / Avantage window roundtrip on a new project copy.

Set AVANTAGE_LIVE_INTEGRATION=1, AVANTAGE_INTEGRATION=1 and
AVANTAGE_VGP_FIXTURE. Start one Avantage instance before running. The test opens
and modifies its own copied document; all existing application documents remain.
"""
import asyncio
import os
from pathlib import Path
import sys

import pytest
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

from avantage_mcp.bridge_client import BridgeClient
from test_native_projects import project_copy
from test_native_project_save import source_hashes

pytestmark = pytest.mark.skipif(
    os.environ.get("AVANTAGE_LIVE_INTEGRATION") != "1"
    or os.environ.get("AVANTAGE_INTEGRATION") != "1"
    or not os.environ.get("AVANTAGE_VGP_FIXTURE"),
    reason="Requires explicit live opt-in, a VGP fixture and a running Avantage",
)


def test_real_mcp_live_edits_fit_repeated_apply_stale_guard_save_and_clear(project_copy, tmp_path):
    initial = source_hashes(project_copy)
    output = tmp_path / "MCP-live-result.vgp"
    cleared = tmp_path / "MCP-cleared-result.vgp"

    async def workflow():
        root = Path(__file__).resolve().parents[1]
        parameters = StdioServerParameters(
            command=sys.executable, args=["-m", "avantage_mcp.server"], cwd=str(root),
            env={**os.environ, "AVANTAGE_BRIDGE_PATH": str(root / "native/AvantageBridge.exe")},
        )
        async with stdio_client(parameters) as (read, write):
            async with ClientSession(read, write) as client:
                await client.initialize()
                assert len((await client.list_tools()).tools) == 42

                async def call(name, **args):
                    if os.environ.get("AVANTAGE_TRACE") == "1":
                        print("MCP", name, flush=True)
                    result = await client.call_tool("avantage_" + name, args)
                    if result.isError:
                        print(str(result.content), flush=True)
                    assert not result.isError, result.content
                    return result.structuredContent

                assert (await call("status"))["native_live_document_bridge_verified"]
                document = (await call("open_processing_document", path=str(project_copy)))["document"]
                rows = (await call("list_document_spectra", document_id=document["document_id"]))["spectra"]
                index = next(row["spectrum_index"] for row in rows if row["visible_in_grid"]
                             and row["name"].startswith("C1s") and row["fitted_peak_count"] >= 2)
                attached = await call("attach_document_spectrum", document_id=document["document_id"], spectrum_index=index)
                stale = await call("attach_document_spectrum", document_id=document["document_id"], spectrum_index=index)
                sid = attached["session_id"]
                first, second = attached["model"]["peaks"][:2]
                await call("set_background", session_id=sid, kind="shirley")
                await call("update_peak", session_id=sid, peak_id=first["peak_id"],
                           patch={"label": "MCP live verified", "gaussian_lorentz": .3})
                await call("set_peak_constraint", session_id=sid, peak_id=first["peak_id"],
                           attribute="gl", constraint={"type": "fixed", "value1": .3})
                await call("set_peak_constraint", session_id=sid, peak_id=second["peak_id"], attribute="gl",
                           constraint={"type": "linked", "reference_peak_id": first["peak_id"], "operator": "multiply", "value1": 1})
                fit = await call("run_fit", session_id=sid, max_iterations=25)
                assert fit["fit_report"]["process_isolated"]
                for _ in range(2):
                    applied = await call("apply_fit_to_document", session_id=sid)
                    for field in ("live_readback_verified", "saved_project_verified", "raw_data_verified", "unrelated_spectra_verified", "grid_bindings_verified"):
                        assert applied[field]
                rejected = await client.call_tool("avantage_apply_fit_to_document", {"session_id": stale["session_id"]})
                assert rejected.isError and "LIVE_DOCUMENT_CHANGED" in str(rejected.content)
                saved = await call("save_processing_document", document_id=applied["document"]["document_id"], path=str(output))
                assert saved["roundtrip_verified"] and saved["all_spectrum_models_verified"]
                current = await call("attach_document_spectrum", document_id=saved["document"]["document_id"], spectrum_index=index)
                await call("clear_fit_model", session_id=current["session_id"])
                empty = await call("apply_fit_to_document", session_id=current["session_id"])
                assert empty["peak_count"] == 0 and empty["live_readback_verified"]
                await call("save_processing_document", document_id=empty["document"]["document_id"], path=str(cleared))
                await call("open_processing_document", path=str(output))
                return index

    index = asyncio.run(workflow())
    # A fresh bridge can still connect after the MCP worker released its app
    # references, and verifies the durable models independently of that worker.
    with BridgeClient(timeout=90) as fresh:
        assert fresh.call("list_processing_documents")["documents"]
        project = fresh.call("open_processing_project", {"path": str(output)})
        selected = fresh.call("open_project_spectrum", {"project_id": project["project_id"], "spectrum_index": index})
        peaks = selected["model"]["peaks"]
        assert peaks[0]["label"] == "MCP live verified"
        assert peaks[1]["constraints"]["gl"]["reference_peak_id"] == peaks[0]["peak_id"]
        project = fresh.call("open_processing_project", {"path": str(cleared)})
        assert fresh.call("open_project_spectrum", {"project_id": project["project_id"], "spectrum_index": index})["model"] is None
    assert source_hashes(project_copy) == initial
