"""Opt-in tests using the licensed local Avantage installation and private test copies.

Run with AVANTAGE_INTEGRATION=1 after scripts/build-native.ps1.
"""

from __future__ import annotations

import hashlib
import os
from pathlib import Path
import shutil
import json
import math

import pytest

from avantage_mcp.bridge_client import BridgeClient, BridgeError


pytestmark = pytest.mark.skipif(
    os.environ.get("AVANTAGE_INTEGRATION") != "1",
    reason="Requires an installed Avantage and opt-in AVANTAGE_INTEGRATION=1",
)


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


@pytest.fixture
def native():
    client = BridgeClient(timeout=60)
    try:
        yield client
    finally:
        client.close()


def test_native_roundtrip_and_source_protection(native, tmp_path):
    home = Path(os.environ.get("AVANTAGE_HOME", r"C:\Program Files\Thermo Scientific\Avantage"))
    sample = home / "Sample Data" / "Avantage Spectra" / "PET1407.DATA" / "C1s.VGD"
    source = tmp_path / "原始谱图.VGD"
    shutil.copy2(sample, source)
    original_hash = digest(source)
    opened = native.call("open_spectrum", {"path": str(source)})
    sid = opened["session_id"]
    before = native.call("get_spectrum_data", {"session_id": sid, "limit": 1000})
    assert before["total"] == 151
    assert before["intensity"][0] == pytest.approx(895.820068359375)
    assert before["binding_energy"][0] == pytest.approx(290, abs=0.001)

    native.call("charge_shift", {"session_id": sid, "delta_ev": 0.8})
    shifted = native.call("get_spectrum_data", {"session_id": sid, "limit": 1000})
    assert shifted["intensity"] == before["intensity"]
    assert shifted["binding_energy"] == pytest.approx([x + 0.8 for x in before["binding_energy"]])

    saved_path = tmp_path / "结果.VGD"
    saved = native.call("save_spectrum", {"session_id": sid, "path": str(saved_path)})
    assert saved["source_unchanged"] is True
    copy = native.call("open_spectrum", {"path": str(saved_path)})
    reread = native.call("get_spectrum_data", {"session_id": copy["session_id"], "limit": 1000})
    assert reread["x"] == shifted["x"]
    assert reread["intensity"] == shifted["intensity"]
    assert reread["binding_energy"] == shifted["binding_energy"]

    alias = tmp_path / "原始别名.VGD"
    os.link(source, alias)
    for target in (source, alias, saved_path):
        with pytest.raises(BridgeError) as error:
            native.call("save_spectrum", {"session_id": sid, "path": str(target), "overwrite": True})
        assert error.value.code == "SOURCE_PROTECTED"
    assert digest(source) == original_hash

    export_path = tmp_path / "光谱.csv"
    native.call("export_spectrum", {"session_id": sid, "path": str(export_path), "format": "csv"})
    lines = export_path.read_text(encoding="utf-8-sig").splitlines()
    assert len(lines) == 152
    assert "binding_energy_eV" in lines[0]
    with pytest.raises(BridgeError) as error:
        native.call("export_spectrum", {"session_id": sid, "path": str(export_path), "format": "csv"})
    assert error.value.code == "FILE_EXISTS"

    for session in (sid, copy["session_id"]):
        native.call("close_session", {"session_id": session})
    with pytest.raises(BridgeError) as error:
        native.call("get_spectrum_info", {"session_id": sid})
    assert error.value.code == "SESSION_NOT_FOUND"


def test_native_backgrounds_curves_and_model_roundtrip(native, tmp_path):
    home = Path(os.environ.get("AVANTAGE_HOME", r"C:\Program Files\Thermo Scientific\Avantage"))
    sample = home / "Sample Data" / "Avantage Spectra" / "PET1407.DATA" / "C1s.VGD"
    sid = native.call("open_spectrum", {"path": str(sample)})["session_id"]
    for kind in ("linear", "shirley", "smart", "tougaard", "simple"):
        native.call("set_background", {"session_id": sid, "kind": kind})
        curve = native.call("get_background_data", {"session_id": sid})["background"]
        assert len(curve) == 151 and all(math.isfinite(x) for x in curve)
    background = native.call("set_background", {"session_id": sid, "kind": "shirley", "start_binding_ev": 282, "end_binding_ev": 288})
    assert background["background"]["first_index"] == 20
    data = native.call("get_background_data", {"session_id": sid})
    assert all(x is None for x in data["background"][:20])
    assert all(x is not None for x in data["background"][20:81])

    one = native.call("add_peak", {"session_id": sid, "peak": {"label": "C-C", "centre_ev": 285, "height": 1000, "fwhm_ev": 1.2}})["peak"]["peak_id"]
    two = native.call("add_peak", {"session_id": sid, "peak": {"label": "C-O", "centre_ev": 286.6, "height": 400, "fwhm_ev": 1.4, "gl_combine_method": "sum"}})["peak"]["peak_id"]
    native.call("set_peak_constraint", {"session_id": sid, "peak_id": two, "attribute": "centre", "constraint": {"type": "linked", "operator": "plus", "value1": 1.6, "reference_peak_id": one}})
    before = native.call("get_model_curve", {"session_id": sid})
    assert len(before["components"]) == 2
    assert before["residual"][0] is None
    assert before["metrics"]["point_count"] == 61
    assert all(math.isfinite(x) for x in before["peak_sum"])
    recipe = tmp_path / "模型.json"
    native.call("export_fit_model", {"session_id": sid, "path": str(recipe)})
    native.call("clear_fit_model", {"session_id": sid})
    imported = native.call("import_fit_model", {"session_id": sid, "path": str(recipe)})
    assert imported["spectrum_matches"] is True
    assert imported["model"]["peak_count"] == 2
    after = native.call("get_model_curve", {"session_id": sid})
    assert before["peak_sum"] == after["peak_sum"]
    assert before["total_model"] == after["total_model"]
    assert imported["model"]["peaks"][1]["constraints"]["centre"]["reference_peak_id"] == imported["model"]["peaks"][0]["peak_id"]
    third = native.call("add_peak", {"session_id": sid, "peak": {"label": "新增峰", "centre_ev": 287.5, "height": 100, "fwhm_ev": 1.0}})
    native_table = native.call("export_peak_table", {"session_id": sid, "path": str(tmp_path / "原生峰表.vgt")})
    assert native_table["roundtrip_verified"] is True
    assert native_table["peak_count"] == 3
    constraints = native_table["peaks"][1]["constraints"]
    linked = [c for c in constraints if c["attribute"] == 2]
    assert len(linked) == 1 and linked[0]["reference_peak_index"] == 0
    inspected = native.call("inspect_peak_table", {"session_id": sid, "path": str(tmp_path / "原生峰表.vgt")})
    assert inspected["peaks"] == native_table["peaks"]
    native.call("export_model_curves", {"session_id": sid, "path": str(tmp_path / "分峰曲线.csv"), "format": "csv"})
    assert len((tmp_path / "分峰曲线.csv").read_text(encoding="utf-8-sig").splitlines()) == 152
    native.call("clear_fit_model", {"session_id": sid})
    native.call("clear_background", {"session_id": sid})
    native.call("charge_shift", {"session_id": sid, "delta_ev": 1})
    with pytest.raises(BridgeError) as error:
        native.call("import_fit_model", {"session_id": sid, "path": str(recipe)})
    assert error.value.code == "SPECTRUM_MISMATCH"
