"""Lossless VGP model-save regressions against explicitly supplied private fixtures."""
from __future__ import annotations

import os
from pathlib import Path

import pytest

from avantage_mcp.bridge_client import BridgeClient, BridgeError
from test_native_projects import (choose_fitted, fingerprint, native, open_project,
                                 project_copy, select, tree_hashes)


pytestmark = pytest.mark.skipif(
    os.environ.get("AVANTAGE_INTEGRATION") != "1" or not os.environ.get("AVANTAGE_VGP_FIXTURE"),
    reason="Requires installed Avantage and an explicitly supplied VGP fixture",
)


def storage_stream(path: Path, *parts: str) -> bytes:
    pythoncom = pytest.importorskip("pythoncom")
    root = pythoncom.StgOpenStorage(str(path), None, 0x20, None, 0)
    current = root
    for name in parts[:-1]:
        current = current.OpenStorage(name, None, 0x10, None, 0)
    stream = current.OpenStream(parts[-1], None, 0x10, 0)
    return stream.Read(stream.Stat()[2])


def comparable_model(model: dict | None):
    if model is None:
        return None
    ids = [peak["peak_id"] for peak in model["peaks"]]
    result = []
    for peak in model["peaks"]:
        values = {key: peak[key] for key in (
            "label", "centre_binding_ev", "centre_kinetic_ev", "fwhm_ev", "height",
            "area", "gaussian_lorentz", "tail_mix", "tail_height", "tail_exponential",
            "native_gl_combine_method",
        )}
        values["constraints"] = {
            attribute: {
                "native": constraint["native"],
                "reference_index": ids.index(constraint["reference_peak_id"])
                if constraint.get("reference_peak_id") else None,
            }
            for attribute, constraint in peak["constraints"].items()
        }
        result.append(values)
    return result


def capture_project(client, project):
    result = {}
    for row in project["spectra"]:
        selected = select(client, project, row)
        sid = selected["session_id"]
        data = client.call("get_spectrum_data", {"session_id": sid, "limit": 100000})
        result[row["spectrum_index"]] = {
            "x": data["x"], "intensity": data["intensity"], "binding_energy": data["binding_energy"],
            "model": comparable_model(selected["model"]), "background": selected["background"],
        }
        client.call("close_session", {"session_id": sid})
    return result


def source_hashes(path: Path):
    return fingerprint(path), tree_hashes(path.with_suffix(".DATA"))


def test_save_project_model_reopens_with_all_other_spectra_and_layout_preserved(
    native, project_copy, tmp_path,
):
    initial = source_hashes(project_copy)
    project = open_project(native, project_copy)
    before = capture_project(native, project)
    row = choose_fitted(project)
    selected = select(native, project, row)
    sid = selected["session_id"]
    peaks = selected["model"]["peaks"]
    native.call("update_peak", {
        "session_id": sid, "peak_id": peaks[0]["peak_id"],
        "patch": {"label": "MCP saved component", "centre_ev": peaks[0]["centre_binding_ev"] + 0.033,
                  "fwhm_ev": peaks[0]["fwhm_ev"] + 0.02, "gaussian_lorentz": 0.35,
                  "tail_mix": 0.12, "tail_height": 0.03, "tail_exponential": 0.25},
    })
    native.call("set_peak_constraint", {
        "session_id": sid, "peak_id": peaks[0]["peak_id"], "attribute": "gl",
        "constraint": {"type": "fixed", "value1": 0.35},
    })
    expected = comparable_model(native.call("get_fit_model", {"session_id": sid})["model"])
    saved = tmp_path / "edited 完整项目.vgp"
    result = native.call("save_project_model", {"session_id": sid, "path": str(saved)})
    for field in ("roundtrip_verified", "source_unchanged", "raw_data_verified",
                  "unrelated_spectra_verified", "grid_layout_preserved", "primary_metadata_preserved"):
        assert result[field] is True
    assert source_hashes(project_copy) == initial
    assert tree_hashes(saved.with_suffix(".DATA")) == initial[1]
    for parts in (("Contents",), ("Grid Cells", "Contents"), ("Grid Cells", "XRefs")):
        assert storage_stream(saved, *parts) == storage_stream(project_copy, *parts)
    # Includes serialized entries that Avantage's usual LoadPeakTable hides when
    # their spectra are not in grid cells. A whole-collection resave used to lose them.
    old_count = int.from_bytes(storage_stream(project_copy, "Fitted Peaks", "Contents")[4:8], "little")
    assert int.from_bytes(storage_stream(saved, "Fitted Peaks", "Contents")[4:8], "little") == old_count
    assert result["retained_serialized_fitted_count"] == old_count
    # A fresh worker proves the VGP itself carries the result after disconnect.
    with BridgeClient(timeout=90) as reopened:
        after = capture_project(reopened, open_project(reopened, saved))
    assert after.keys() == before.keys()
    for index in before:
        for field in ("x", "intensity", "binding_energy"):
            assert after[index][field] == before[index][field]
        if index == row["spectrum_index"]:
            assert after[index]["model"] == expected
        else:
            assert after[index] == before[index]


def test_save_project_model_adds_and_deletes_components_without_losing_hidden_entries(
    native, project_copy, tmp_path,
):
    initial = source_hashes(project_copy)
    project = open_project(native, project_copy)
    row = choose_fitted(project)
    selected = select(native, project, row)
    sid = selected["session_id"]
    peaks = selected["model"]["peaks"]
    native.call("remove_peak", {"session_id": sid, "peak_id": peaks[-1]["peak_id"]})
    new_peak = native.call("add_peak", {
        "session_id": sid,
        "peak": {"label": "New MCP component", "centre_ev": peaks[0]["centre_binding_ev"] + 1.1,
                 "height": peaks[0]["height"] * 0.05, "fwhm_ev": 1.1},
    })["peak"]
    native.call("set_peak_constraint", {
        "session_id": sid, "peak_id": new_peak["peak_id"], "attribute": "fwhm",
        "constraint": {"type": "linked", "reference_peak_id": peaks[0]["peak_id"], "operator": "multiply", "value1": 1},
    })
    expected = comparable_model(native.call("get_fit_model", {"session_id": sid})["model"])
    saved = tmp_path / "added-and-deleted.vgp"
    assert native.call("save_project_model", {"session_id": sid, "path": str(saved)})["roundtrip_verified"]
    with BridgeClient(timeout=90) as reopened:
        selected = select(reopened, open_project(reopened, saved), row)
        assert comparable_model(selected["model"]) == expected
    assert source_hashes(project_copy) == initial


def test_save_project_model_protects_sources_and_existing_companions(native, project_copy, tmp_path):
    project = open_project(native, project_copy)
    selected = select(native, project, choose_fitted(project))
    args = {"session_id": selected["session_id"], "path": str(project_copy)}
    with pytest.raises(BridgeError, match="SOURCE_PROTECTED"):
        native.call("save_project_model", args)
    existing = tmp_path / "existing.vgp"
    existing.write_bytes(b"preserve this existing file")
    with pytest.raises(BridgeError, match="FILE_EXISTS"):
        native.call("save_project_model", {**args, "path": str(existing)})
    assert existing.read_bytes() == b"preserve this existing file"
    occupied = tmp_path / "occupied.vgp"
    occupied.with_suffix(".DATA").mkdir()
    with pytest.raises(BridgeError, match="FILE_EXISTS"):
        native.call("save_project_model", {**args, "path": str(occupied)})
    assert not occupied.exists()


def test_save_project_model_rejects_changed_raw_energy_axis(native, project_copy, tmp_path):
    project = open_project(native, project_copy)
    unfitted = next((row for row in project["spectra"] if not row["fitted_peak_count"]), None)
    if unfitted is None:
        pytest.skip("A raw region is required to test calibration-before-model save")
    selected = select(native, project, unfitted)
    sid = selected["session_id"]
    native.call("charge_shift", {"session_id": sid, "delta_ev": 0.1})
    data = native.call("get_spectrum_data", {"session_id": sid, "limit": 100000})
    native.call("set_background", {"session_id": sid, "kind": "linear"})
    native.call("add_peak", {"session_id": sid, "peak": {
        "centre_ev": data["binding_energy"][len(data["binding_energy"]) // 2],
        "fwhm_ev": 1.0, "height": 100.0,
    }})
    saved = tmp_path / "changed-axis.vgp"
    with pytest.raises(BridgeError, match="PROJECT_DATA_CHANGED"):
        native.call("save_project_model", {"session_id": sid, "path": str(saved)})
    assert not saved.exists() and not saved.with_suffix(".DATA").exists()


def test_save_project_model_clears_one_region_and_retains_every_other_model(
    native, project_copy, tmp_path,
):
    project = open_project(native, project_copy)
    before = capture_project(native, project)
    row = choose_fitted(project)
    selected = select(native, project, row)
    sid = selected["session_id"]
    native.call("clear_fit_model", {"session_id": sid})
    saved = tmp_path / "cleared-region.vgp"
    assert native.call("save_project_model", {"session_id": sid, "path": str(saved)})["roundtrip_verified"]
    with BridgeClient(timeout=90) as reopened:
        after = capture_project(reopened, open_project(reopened, saved))
    for index, original in before.items():
        if index == row["spectrum_index"]:
            assert after[index]["model"] is None
            for field in ("x", "intensity", "binding_energy"):
                assert after[index][field] == original[field]
        else:
            assert after[index] == original


def test_missing_project_spectrum_reports_error_without_poisoning_worker(
    native, project_copy,
):
    companion = project_copy.with_suffix(".DATA")
    missing = next(file for file in companion.iterdir() if file.suffix.lower() == ".vgd")
    data = missing.read_bytes()
    missing.unlink()
    with pytest.raises(BridgeError, match="COMPANION_DATA_MISSING"):
        open_project(native, project_copy)
    missing.write_bytes(data)
    assert open_project(native, project_copy)["project"]["spectrum_count"] > 0


def test_save_project_model_replaces_a_background_and_builds_a_new_native_model(
    native, project_copy, tmp_path,
):
    project = open_project(native, project_copy)
    before = capture_project(native, project)
    row = choose_fitted(project)
    selected = select(native, project, row)
    sid = selected["session_id"]
    initial = selected["model"]["peaks"]
    native.call("clear_fit_model", {"session_id": sid})
    native.call("set_background", {"session_id": sid, "kind": "shirley",
                                    "shirley_iterations": 80, "shirley_convergence": 0.4,
                                    "shirley_smooth": True})
    for index, original in enumerate(initial[:2]):
        new = native.call("add_peak", {"session_id": sid, "peak": {
            "label": f"New Shirley component {index + 1}",
            "centre_ev": original["centre_binding_ev"], "fwhm_ev": original["fwhm_ev"],
            "height": original["height"], "gl_combine_method": "sum",
            "gaussian_lorentz": 0.35,
        }})["peak"]
        native.call("set_peak_constraint", {"session_id": sid, "peak_id": new["peak_id"],
                                             "attribute": "fwhm", "constraint": {
                                                 "type": "range", "value1": 0.5, "value2": 3.5,
                                             }})
    expected = native.call("get_fit_model", {"session_id": sid})
    saved = tmp_path / "new-shirley-model.vgp"
    assert native.call("save_project_model", {"session_id": sid, "path": str(saved)})["roundtrip_verified"]
    with BridgeClient(timeout=90) as reopened:
        after = capture_project(reopened, open_project(reopened, saved))
    for index, original in before.items():
        if index == row["spectrum_index"]:
            assert after[index]["model"] == comparable_model(expected["model"])
            assert after[index]["background"] == expected["background"]
        else:
            assert after[index] == original
