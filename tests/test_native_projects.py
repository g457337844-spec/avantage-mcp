"""Opt-in VGP tests. Supplied fixtures are copied before every native operation.

Set AVANTAGE_INTEGRATION=1 and AVANTAGE_VGP_FIXTURE to a processing project
with its matching .DATA directory. No user's spectra are included in this repo.
"""

from __future__ import annotations

import hashlib
import os
from pathlib import Path
import shutil

import pytest

from avantage_mcp.bridge_client import BridgeClient, BridgeError


pytestmark = pytest.mark.skipif(
    os.environ.get("AVANTAGE_INTEGRATION") != "1" or not os.environ.get("AVANTAGE_VGP_FIXTURE"),
    reason="Requires installed Avantage and an explicitly supplied VGP fixture",
)


def fingerprint(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def tree_hashes(path: Path) -> dict[str, str]:
    return {str(file.relative_to(path)): fingerprint(file) for file in path.rglob("*") if file.is_file()}


@pytest.fixture
def project_copy(tmp_path):
    original = Path(os.environ["AVANTAGE_VGP_FIXTURE"])
    companion = original.with_suffix(".DATA")
    if not original.is_file() or not companion.is_dir():
        pytest.skip("The supplied VGP fixture or matching .DATA directory is missing")
    copy = tmp_path / original.name
    shutil.copy2(original, copy)
    shutil.copytree(companion, copy.with_suffix(".DATA"))
    return copy


@pytest.fixture
def native():
    with BridgeClient(timeout=60) as client:
        yield client


def open_project(native, source):
    return native.call("open_processing_project", {"path": str(source)})


def choose_fitted(project):
    candidates = [row for row in project["spectra"] if row["fitted_peak_count"]]
    if not candidates:
        pytest.skip("This fixture contains no fitted peak model")
    return next((row for row in candidates if "C1s" in row["name"]), candidates[0])


def select(native, project, spectrum):
    return native.call("open_project_spectrum", {
        "project_id": project["project_id"], "spectrum_index": spectrum["spectrum_index"],
    })


def test_vgp_native_reads_real_data_and_models(native, project_copy):
    initial = tree_hashes(project_copy.parent)
    project = open_project(native, project_copy)
    assert project["project"]["spectrum_count"] == len(project["spectra"]) > 0
    assert project["project"]["fitted_peak_count"] == sum(row["fitted_peak_count"] for row in project["spectra"])
    listed = native.call("list_project_spectra", {"project_id": project["project_id"]})
    assert listed["spectra"] == project["spectra"]
    selected = select(native, project, choose_fitted(project))
    assert selected["private_snapshot_verified"] is True
    sid = selected["session_id"]
    copied = native.call("get_spectrum_data", {"session_id": sid, "limit": 100000})
    companion_file = project_copy.with_suffix(".DATA") / Path(selected["spectrum"]["source_path"]).name
    source_sid = native.call("open_spectrum", {"path": str(companion_file)})["session_id"]
    original = native.call("get_spectrum_data", {"session_id": source_sid, "limit": 100000})
    for field in ("x", "intensity", "binding_energy"):
        assert copied[field] == original[field]
    assert any(value != 0 for value in copied["intensity"])
    assert selected["model"]["peak_count"] == selected["spectrum"]["fitted_peak_count"]
    assert selected["background"] is not None
    assert tree_hashes(project_copy.parent) == initial


def test_project_edits_are_private_and_survive_project_close(native, project_copy, tmp_path):
    initial = tree_hashes(project_copy.parent)
    project = open_project(native, project_copy)
    row = choose_fitted(project)
    selected = select(native, project, row)
    sid = selected["session_id"]
    peak = selected["model"]["peaks"][0]
    native.call("update_peak", {"session_id": sid, "peak_id": peak["peak_id"],
                               "patch": {"label": "Private regression peak", "centre_ev": peak["centre_binding_ev"] + 0.03}})
    repeated = select(native, project, row)
    assert repeated["model"]["peaks"][0]["label"] == peak["label"]
    assert repeated["model"]["peaks"][0]["centre_binding_ev"] == pytest.approx(peak["centre_binding_ev"])
    native.call("close_processing_project", {"project_id": project["project_id"]})
    model = native.call("get_fit_model", {"session_id": sid})["model"]
    assert model["peaks"][0]["label"] == "Private regression peak"
    assert native.call("get_model_curve", {"session_id": sid, "limit": 1})["count"] == 1
    assert tree_hashes(project_copy.parent) == initial
    saved = tmp_path / "independent-spectrum.vgd"
    result = native.call("save_spectrum", {"session_id": sid, "path": str(saved)})
    assert result["source_unchanged"] is True


def test_project_peak_model_preserves_native_constraints_on_vgt_roundtrip(native, project_copy, tmp_path):
    project = open_project(native, project_copy)
    selected = select(native, project, choose_fitted(project))
    sid = selected["session_id"]
    before = selected["model"]
    output = tmp_path / "model.vgt"
    export = native.call("export_peak_table", {"session_id": sid, "path": str(output)})
    assert export["roundtrip_verified"] is True
    native.call("clear_fit_model", {"session_id": sid})
    native.call("import_peak_table", {"session_id": sid, "path": str(output)})
    after = native.call("get_fit_model", {"session_id": sid})["model"]
    assert after["peak_count"] == before["peak_count"]
    for expected, actual in zip(before["peaks"], after["peaks"]):
        for field in ("label", "centre_binding_ev", "fwhm_ev", "height", "gaussian_lorentz",
                      "tail_mix", "tail_height", "tail_exponential", "native_gl_combine_method"):
            assert actual[field] == expected[field]
        assert set(actual["constraints"]) == set(expected["constraints"])
        for attr, constraint in expected["constraints"].items():
            assert actual["constraints"][attr]["native"] == constraint["native"]


def test_missing_companion_and_malformed_project_do_not_poison_worker(native, project_copy, tmp_path):
    empty = tmp_path / "empty.vgp"
    empty.write_bytes(b"not a native processing document")
    with pytest.raises(BridgeError) as error:
        open_project(native, empty)
    assert error.value.code == "COMPANION_DATA_MISSING"
    (tmp_path / "empty.DATA").mkdir()
    with pytest.raises(BridgeError) as error:
        open_project(native, empty)
    assert error.value.code == "INVALID_PROJECT"
    assert open_project(native, project_copy)["project"]["spectrum_count"] > 0
