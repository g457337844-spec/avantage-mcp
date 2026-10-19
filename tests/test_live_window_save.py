"""Explicitly authorized disposable-window integration; never opens documents.

Requires AVANTAGE_LIVE_WRITE_TEST=1, AVANTAGE_LIVE_WRITE_FIXTURE, and a
same-basename .mcp-test.json marker containing purpose=disposable-live-test.
The fixture must already be open. Only its peak label is changed and restored.
"""
import json
import os
from pathlib import Path

import pytest

from avantage_mcp.bridge_client import BridgeClient, BridgeError
from avantage_mcp.project_writeback import apply_document_model, manifest, save_document_in_place

pytestmark=pytest.mark.skipif(
    os.environ.get("AVANTAGE_LIVE_WRITE_TEST")!="1"
    or not os.environ.get("AVANTAGE_LIVE_WRITE_FIXTURE"),
    reason="Requires explicitly marked disposable fixture already open in Avantage",
)


def test_existing_window_save_twice_and_unchanged_save():
    path=Path(os.environ["AVANTAGE_LIVE_WRITE_FIXTURE"]).resolve()
    marker=path.with_suffix(".mcp-test.json")
    assert marker.is_file(), "Refusing live writes without a disposable fixture marker"
    identity=json.loads(marker.read_text(encoding="utf-8"))
    assert identity=={"purpose":"disposable-live-test","path":str(path)}
    with BridgeClient(timeout=90) as client:
        status=client.call("status")
        assert status["native_document_lifecycle_verified"] is True
        documents=client.call("list_processing_documents")["documents"]
        document=next(d for d in documents if Path(d["path"]).resolve()==path)
        document_id=document["document_id"]
        rows=client.call("list_document_spectra",{"document_id":document_id})["spectra"]
        row=next(r for r in rows if r["fitted_peak_count"]>0)
        original_label=None
        for iteration in range(2):
            before=manifest(path)
            attached=client.call("attach_document_spectrum",{"document_id":document_id,"spectrum_index":row["spectrum_index"]})
            session=attached["session_id"];peak=attached["model"]["peaks"][0]
            if original_label is None:original_label=peak["label"]
            label="MCP disposable save verification" if iteration==0 else original_label
            client.call("update_peak",{"session_id":session,"peak_id":peak["peak_id"],"patch":{"label":label}})
            saved=apply_document_model(client,session)
            assert saved["saved_project_verified"] and saved["original_save"]["same_window_retained"]
            assert manifest(Path(saved["backup_path"]))==before
            with pytest.raises(BridgeError,match="LIVE_DOCUMENT_CHANGED"):
                client.call("apply_fit_to_document",{"session_id":session})
            client.call("close_session",{"session_id":session})
            after=client.call("list_processing_documents")["documents"]
            assert {(d["document_id"],d["path"]) for d in after}=={(d["document_id"],d["path"]) for d in documents}
        before=manifest(path)
        unchanged=save_document_in_place(client,document_id,str(path))
        assert unchanged["already_saved"] and not unchanged["normal_menu_save_sent"]
        assert manifest(path)==before
