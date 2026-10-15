"""Run a complete, real MCP workflow against the installed Avantage sample.

Run with the project's .venv Python. Outputs go to --output-dir (absolute path).
These numerical initial peaks demonstrate automation, not chemical assignments.
"""
import argparse
import asyncio
import hashlib
import json
import os
from pathlib import Path
import sys

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


async def workflow(destination: Path):
    destination.mkdir(parents=True, exist_ok=True)
    project = Path(__file__).resolve().parents[1]
    parameters = StdioServerParameters(command=sys.executable, args=["-m", "avantage_mcp.server"], cwd=str(project), env={**os.environ, "AVANTAGE_BRIDGE_PATH": str(project / "native/AvantageBridge.exe")})
    async with stdio_client(parameters) as (read, write):
        async with ClientSession(read, write) as client:
            await client.initialize()
            tools = await client.list_tools()
            assert len(tools.tools) == 42

            async def call(name, **arguments):
                response = await client.call_tool("avantage_" + name, arguments)
                if response.isError:
                    raise RuntimeError(response.content)
                return response.structuredContent

            status = await call("status")
            assert status["native_fit_solver_verified"]
            samples = await call("list_sample_files")
            source = Path(next(f["path"] for f in samples["files"] if "PET1407.DATA" in f["path"] and f["name"].lower() == "c1s.vgd"))
            initial_hash = hashlib.sha256(source.read_bytes()).hexdigest()
            sid = (await call("open_spectrum", path=str(source)))["session_id"]
            await call("set_background", session_id=sid, kind="linear")
            ids = []
            for index, (centre, height, width) in enumerate(((284.1, 4000, 1.2), (281.7, 4000, 1.1), (280, 14000, 1.3)), 1):
                peak = (await call("add_peak", session_id=sid, peak={"label": f"Demo peak {index}", "centre_ev": centre, "height": height, "fwhm_ev": width}))["peak"]
                ids.append(peak["peak_id"])
            await call("set_peak_constraint", session_id=sid, peak_id=ids[0], attribute="gl", constraint={"type": "fixed", "value1": .3})
            await call("set_peak_constraint", session_id=sid, peak_id=ids[0], attribute="fwhm", constraint={"type": "range", "value1": .8, "value2": 1.4})
            await call("set_peak_constraint", session_id=sid, peak_id=ids[1], attribute="centre", constraint={"type": "linked", "reference_peak_id": ids[0], "operator": "minus", "value1": 2.4})
            fitted = await call("run_fit", session_id=sid, asymmetric=False)
            assert fitted["fit_report"]["converged"]
            curve = await call("get_model_curve", session_id=sid, asymmetric=False)
            paths = {"vgd": destination / "C1s_谱图.VGD", "vgt": destination / "C1s_分峰.VGT", "recipe": destination / "C1s_模型.json", "csv": destination / "C1s_分峰曲线.csv", "curves": destination / "C1s_分峰曲线.json"}
            saved = await call("save_spectrum", session_id=sid, path=str(paths["vgd"]), overwrite=True)
            table = await call("export_peak_table", session_id=sid, path=str(paths["vgt"]), overwrite=True)
            inspected = await call("inspect_peak_table", session_id=sid, path=str(paths["vgt"]))
            await call("export_fit_model", session_id=sid, path=str(paths["recipe"]), overwrite=True)
            await call("export_model_curves", session_id=sid, path=str(paths["csv"]), format="csv", overwrite=True, asymmetric=False)
            await call("export_model_curves", session_id=sid, path=str(paths["curves"]), format="json", overwrite=True, asymmetric=False)
            other = (await call("open_spectrum", path=str(paths["vgd"])))["session_id"]
            imported = await call("import_peak_table", session_id=other, path=str(paths["vgt"]))
            restored = await call("get_model_curve", session_id=other, asymmetric=False)
            assert abs(restored["metrics"]["residual_sum_squares"] - curve["metrics"]["residual_sum_squares"]) < 1e-5
            assert imported["model"]["peak_count"] == 3
            await call("import_fit_model", session_id=other, path=str(paths["recipe"]))
            assert (await call("get_model_curve", session_id=other, asymmetric=False))["metrics"] == curve["metrics"]
            await call("close_session", session_id=other)
            await call("close_session", session_id=sid)
            report = {"date": "2026-10-13", "tool_count": len(tools.tools), "tools": [tool.name for tool in tools.tools], "status": status, "source_path": str(source), "source_sha256": initial_hash, "source_unchanged": initial_hash == hashlib.sha256(source.read_bytes()).hexdigest(), "fit": fitted, "metrics": curve["metrics"], "native_vgt_roundtrip_verified": table["roundtrip_verified"], "vgt_inspected_peak_count": inspected["peak_count"], "vgd_vgt_reload_preserves_metrics": True, "json_recipe_reload_preserves_metrics": True, "saved_spectrum": saved, "artifacts": {key: str(path) for key, path in paths.items()}, "scientific_scope": "Numerical automation demonstration using vendor sample data; no chemical peak assignments asserted."}
            (destination / "verification.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
            print(json.dumps({"ok": True, "tool_count": len(tools.tools), "converged": fitted["fit_report"]["converged"], "vgt_roundtrip": True, "source_unchanged": report["source_unchanged"], "output": str(destination)}, ensure_ascii=False))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    asyncio.run(workflow(args.output_dir.resolve()))
