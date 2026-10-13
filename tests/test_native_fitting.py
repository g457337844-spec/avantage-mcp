"""Real native fitting, process isolation and VGT roundtrip regressions."""
import hashlib
import os
from pathlib import Path

import pytest
from avantage_mcp.bridge_client import BridgeClient, BridgeError

pytestmark = pytest.mark.skipif(os.environ.get("AVANTAGE_INTEGRATION") != "1", reason="Requires installed Avantage and AVANTAGE_INTEGRATION=1")
SAMPLE = Path(os.environ.get("AVANTAGE_HOME", r"C:\Program Files\Thermo Scientific\Avantage")) / "Sample Data/Avantage Spectra/PET1407.DATA/C1s.VGD"


def prepare(client, kind="linear", **background):
    sid = client.call("open_spectrum", {"path": str(SAMPLE)})["session_id"]
    client.call("set_background", {"session_id": sid, "kind": kind, **background})
    ids = []
    for centre, height, width in ((284.1, 4000, 1.2), (281.7, 4000, 1.1), (280, 14000, 1.3)):
        peak = client.call("add_peak", {"session_id": sid, "peak": {"label": str(centre), "centre_ev": centre, "height": height, "fwhm_ev": width}})["peak"]
        ids.append(peak["peak_id"])
    client.call("set_peak_constraint", {"session_id": sid, "peak_id": ids[0], "attribute": "gl", "constraint": {"type": "fixed", "value1": .3}})
    client.call("set_peak_constraint", {"session_id": sid, "peak_id": ids[0], "attribute": "fwhm", "constraint": {"type": "range", "value1": .8, "value2": 1.4}})
    client.call("set_peak_constraint", {"session_id": sid, "peak_id": ids[1], "attribute": "centre", "constraint": {"type": "linked", "reference_peak_id": ids[0], "operator": "minus", "value1": 2.4}})
    return sid, ids


@pytest.mark.parametrize("asymmetric", [False, True])
def test_repeated_native_fits_preserve_peak_ids_and_constraints(asymmetric):
    source_hash = hashlib.sha256(SAMPLE.read_bytes()).hexdigest()
    with BridgeClient(timeout=120) as client:
        for _ in range(2):
            sid, ids = prepare(client)
            worker_pid = client.pid
            fitted = client.call("run_fit", {"session_id": sid, "asymmetric": asymmetric})
            report, peaks = fitted["fit_report"], fitted["model"]["peaks"]
            assert report["converged"] and report["process_isolated"]
            assert report["residual_sse_after"] < report["residual_sse_before"]
            assert [p["peak_id"] for p in peaks] == ids
            assert peaks[0]["gaussian_lorentz"] == pytest.approx(.3)
            assert .8 <= peaks[0]["fwhm_ev"] <= 1.4
            assert peaks[0]["centre_binding_ev"] - peaks[1]["centre_binding_ev"] == pytest.approx(2.4, abs=1e-8)
            assert peaks[1]["constraints"]["centre"]["reference_peak_id"] == ids[0]
            curves = client.call("get_model_curve", {"session_id": sid, "asymmetric": asymmetric})
            assert curves["metrics"]["residual_sum_squares"] == pytest.approx(report["residual_sse_after"], rel=1e-8)
            # Reuse the same spectrum/model in the same persistent worker.
            client.call("update_peak", {"session_id": sid, "peak_id": ids[2], "patch": {"height": peaks[2]["height"] * .95}})
            again = client.call("run_fit", {"session_id": sid, "asymmetric": asymmetric})
            assert again["model"]["fit_executed"]
            assert client.pid == worker_pid
            client.call("close_session", {"session_id": sid})
    assert hashlib.sha256(SAMPLE.read_bytes()).hexdigest() == source_hash


def test_vgt_fit_roundtrip_and_failed_import_preserves_model(tmp_path):
    with BridgeClient(timeout=120) as client:
        sid, ids = prepare(client)
        client.call("run_fit", {"session_id": sid})
        before = client.call("get_model_curve", {"session_id": sid})
        path = tmp_path / "原生分峰结果.VGT"
        saved = client.call("export_peak_table", {"session_id": sid, "path": str(path)})
        assert saved["roundtrip_verified"] and saved["peak_count"] == 3
        inspected = client.call("inspect_peak_table", {"session_id": sid, "path": str(path)})
        assert len(inspected["peaks"][0]["constraints"]) == 2
        client.call("clear_fit_model", {"session_id": sid})
        imported = client.call("import_peak_table", {"session_id": sid, "path": str(path)})
        peaks = imported["model"]["peaks"]
        assert not imported["model"]["fit_executed"]
        assert peaks[1]["constraints"]["centre"]["reference_peak_id"] == peaks[0]["peak_id"]
        assert peaks[0]["peak_id"] not in ids
        after = client.call("get_model_curve", {"session_id": sid})
        assert after["metrics"] == pytest.approx(before["metrics"])
        invalid = tmp_path / "损坏峰表.VGT"
        invalid.write_bytes(b"invalid peak table")
        old = client.call("get_fit_model", {"session_id": sid})
        with pytest.raises(BridgeError):
            client.call("import_peak_table", {"session_id": sid, "path": str(invalid)})
        assert client.call("get_fit_model", {"session_id": sid}) == old
        invalid.write_bytes(bytes.fromhex("D0CF11E0A1B11AE1") + b"truncated")
        with pytest.raises(BridgeError, match="INVALID_PEAK_TABLE"):
            client.call("import_peak_table", {"session_id": sid, "path": str(invalid)})
        assert client.call("get_fit_model", {"session_id": sid}) == old
        # Imported genuine native objects and constraints remain usable for a new fit.
        assert client.call("run_fit", {"session_id": sid})["model"]["fit_executed"]


def test_iteration_limit_and_restricted_background():
    with BridgeClient(timeout=120) as client:
        sid, _ = prepare(client, "shirley", start_binding_ev=278, end_binding_ev=287)
        limited = client.call("run_fit", {"session_id": sid, "max_iterations": 1})
        report = limited["fit_report"]
        assert report["reached_iteration_limit"] and not report["converged"]
        assert report["stop_reason"] == "iteration_limit"
        assert report["fitted_point_count"] < 151
        curve = client.call("get_model_curve", {"session_id": sid})
        assert any(v is None for v in curve["residual"])
        assert curve["metrics"]["residual_sum_squares"] == pytest.approx(report["residual_sse_after"], rel=1e-8)


def test_fit_failure_retains_model_and_session():
    with BridgeClient(timeout=120) as client:
        sid, ids = prepare(client)
        client.call("update_peak", {"session_id": sid, "peak_id": ids[1], "patch": {"gl_combine_method": "sum"}})
        before = client.call("get_fit_model", {"session_id": sid})
        pid = client.pid
        with pytest.raises(BridgeError, match="MIXED_PEAK_METHODS"):
            client.call("run_fit", {"session_id": sid})
        assert client.pid == pid
        assert client.call("get_fit_model", {"session_id": sid}) == before


@pytest.mark.parametrize("kind", ["shirley", "smart", "tougaard", "simple"])
def test_all_backgrounds_native_fit_and_vgt_reload(kind, tmp_path):
    with BridgeClient(timeout=120) as client:
        sid, _ = prepare(client, kind)
        result = client.call("run_fit", {"session_id": sid, "max_iterations": 25})
        assert result["fit_report"]["stop_reason"] in {"iteration_limit", "tolerance"}
        before = client.call("get_model_curve", {"session_id": sid})
        path = tmp_path / f"{kind}.VGT"
        assert client.call("export_peak_table", {"session_id": sid, "path": str(path)})["roundtrip_verified"]
        restored = client.call("import_peak_table", {"session_id": sid, "path": str(path)})
        assert restored["background"]["kind"] == kind
        assert client.call("get_model_curve", {"session_id": sid})["metrics"] == pytest.approx(before["metrics"])


def test_all_fixed_fit_has_clear_error_and_preserves_model():
    with BridgeClient(timeout=120) as client:
        sid, _ = prepare(client)
        peaks = client.call("get_fit_model", {"session_id": sid})["model"]["peaks"]
        for peak in peaks:
            for attribute, key in (("centre", "centre_binding_ev"), ("height", "height"), ("fwhm", "fwhm_ev"), ("gl", "gaussian_lorentz")):
                client.call("set_peak_constraint", {"session_id": sid, "peak_id": peak["peak_id"], "attribute": attribute, "constraint": {"type": "fixed", "value1": peak[key]}})
        before = client.call("get_fit_model", {"session_id": sid})
        with pytest.raises(BridgeError, match="NO_FREE_PARAMETERS"):
            client.call("run_fit", {"session_id": sid})
        assert client.call("get_fit_model", {"session_id": sid}) == before


@pytest.mark.parametrize("method,index", [("sum",1),("convolution",2)])
def test_native_fit_gl_combination_methods(method,index):
    with BridgeClient(timeout=120) as client:
        sid,ids=prepare(client)
        for peak_id in ids:
            client.call("update_peak", {"session_id":sid,"peak_id":peak_id,"patch":{"gl_combine_method":method}})
        fitted=client.call("run_fit", {"session_id":sid,"max_iterations":25})
        assert all(p["native_gl_combine_method"]==index for p in fitted["model"]["peaks"])
        curve=client.call("get_model_curve", {"session_id":sid})
        assert curve["metrics"]["residual_sum_squares"]==pytest.approx(fitted["fit_report"]["residual_sse_after"],rel=1e-8)
