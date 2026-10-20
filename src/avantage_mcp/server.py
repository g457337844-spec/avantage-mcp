"""Thermo Scientific Avantage tools exposed with the official MCP 1.x SDK."""

from __future__ import annotations

import argparse
import asyncio
from contextlib import asynccontextmanager
import json
import os
from typing import Any, AsyncIterator

from mcp.server.fastmcp import FastMCP
from mcp.server.fastmcp.exceptions import ToolError
from mcp.types import ToolAnnotations

from . import __version__, workspace
from .bridge_client import BridgeClient, BridgeError
from .project_writeback import apply_document_model, save_document_in_place, save_project_model_in_place
from .schemas import (
    BackgroundKind, ConstraintSpec, EndpointAverage, EnergyShift, ExportFormat,
    DocumentId, FilePath, Finite, FitIterations, FitTimeout, Nonnegative, Offset, PageSize, PeakAttribute, PeakId, PeakPatch, PeakSpec,
    Positive, ProjectId, SessionId, ShirleyIterations, SpectrumIndex, TougaardType,
    BackupDays, PageLimit, StaleHours,
)


READ = ToolAnnotations(readOnlyHint=True, destructiveHint=False, idempotentHint=True, openWorldHint=False)
SESSION = ToolAnnotations(readOnlyHint=False, destructiveHint=False, idempotentHint=False, openWorldHint=False)
WRITE = ToolAnnotations(readOnlyHint=False, destructiveHint=True, idempotentHint=False, openWorldHint=False)
HOUSEKEEPING = ToolAnnotations(readOnlyHint=False, destructiveHint=True, idempotentHint=True, openWorldHint=False)


#: Remediation appended to a native error so the caller learns what to do next.
#: Keyed by codes the bridge and the writeback layer actually emit.
ERROR_HINTS: dict[str, str] = {
    "SESSION_NOT_FOUND": "Reopen the spectrum or reattach the document; session ids do not survive a bridge restart.",
    "SESSION_INVALIDATED": "Reopen the spectrum or reattach the document before continuing.",
    "PROJECT_INVALIDATED": "Reopen the VGP with avantage_open_processing_project.",
    "DOCUMENT_INVALIDATED": "Call avantage_list_processing_documents, then attach the spectrum again.",
    "SESSION_LIMIT": "Close unused sessions with avantage_list_sessions and avantage_close_session.",
    "LIVE_OPERATION_BUSY": "Another MCP process holds the Avantage transaction. Wait for it to finish; do not auto-retry a write.",
    "APP_MODAL_OR_STARTING": "Close the modal dialog or wait for Avantage to finish starting, then repeat the read.",
    "APP_NOT_RESPONDING": "Avantage is hung. Restore the application manually; no request was sent.",
    "LIVE_DOCUMENT_BRIDGE_NOT_VERIFIED": "This build cannot drive live documents. Use a private VGD session or a VGP project snapshot.",
    "PROJECT_SAVE_NOT_VERIFIED": "Complete VGP saving is unavailable for this build; export a VGT or a JSON recipe instead.",
    "DOCUMENT_LIFECYCLE_NOT_VERIFIED": "The bridge cannot save the original window safely; save a copy with avantage_save_spectrum.",
    "ORIGINAL_IN_USE": "Close the original document in Avantage without saving, then repeat the writeback.",
    "ORIGINAL_PATH_REQUIRED": "The session points at a private intermediate. Reattach the original VGP first.",
    "ORIGINAL_CHANGED": "The original file changed underneath the operation. Inspect it before retrying.",
    "ORIGINAL_SAVE_UNVERIFIED": "The save did not verify. Inspect the original and the retained backup before retrying.",
    "SOURCE_PROTECTED": "Choose a different destination; open sources and working copies cannot be overwritten.",
    "FILE_EXISTS": "Choose a new name or pass overwrite=true explicitly.",
    "MODEL_ALREADY_DEFINED": "Clear the peak model before changing the background, or clear both before charge_shift.",
    "BACKGROUND_REQUIRED": "Call avantage_set_background before avantage_run_fit.",
    "PEAKS_REQUIRED": "Call avantage_add_peak at least once before avantage_run_fit.",
    "SPECTRUM_NOT_FOUND": "Use an index returned by the matching list tool.",
    "UNSUPPORTED_BUILD": "The installed Avantage build is not one this bridge was verified against.",
    "NATIVE_FIT_TIMEOUT": "The fit exceeded its own timeout. The model was retained; retry with a longer timeout_seconds.",
    "NATIVE_FIT_CRASH": "The isolated fit host failed. The model was retained; inspect stderr in the details.",
    "BRIDGE_TIMEOUT": "The native worker exceeded its timeout; the outcome is unknown. Inspect any destination file before retrying.",
    "BRIDGE_EXITED": "The native worker exited. Reopen projects, spectra and documents.",
    "BRIDGE_START_FAILED": "The native bridge could not start. Check AVANTAGE_BRIDGE_PATH.",
    "CLIENT_CLOSED": "The server is shutting down; reconnect before issuing more calls.",
}


def with_hint(code: str, message: str) -> str:
    """Append the remediation for ``code`` when one is known."""
    hint = ERROR_HINTS.get(code)
    if not hint or hint in message:
        return message
    return f"{message} Next: {hint}"


#: Capability string -> the tool family it unlocks. Used to explain a status.
CAPABILITY_MEANING: dict[str, str] = {
    "native_peak_fit": "avantage_run_fit works",
    "native_vgp_save_model": "avantage_save_project_model can replace an original VGP",
    "native_vgp_read": "avantage_open_processing_project works",
    "native_vgt_export": "avantage_export_peak_table works",
    "native_vgt_import": "avantage_import_peak_table works",
    "native_vgt_inspect": "avantage_inspect_peak_table works",
    "live_document_read": "avantage_list_processing_documents and attach work",
    "live_document_apply_existing_parameters": "avantage_apply_fit_to_document works",
    "live_document_save_original": "original-window saving works",
}


def decorate_status(status: dict[str, Any]) -> dict[str, Any]:
    """Make the actionable capabilities explicit instead of burying them.

    The native payload reports `application.connected`, which only covers the
    legacy acquisition-automation interface and reads false on a healthy
    interactive installation. Surface the flags that actually gate tool use.
    """
    result = dict(status)
    result["native_bridge_version"] = result.get("server_version")
    result["server_version"] = __version__
    capabilities = result.get("capabilities")
    if isinstance(capabilities, list):
        result["capability_meaning"] = {
            name: CAPABILITY_MEANING[name] for name in capabilities if name in CAPABILITY_MEANING
        }
    application = result.get("application")
    if isinstance(application, dict):
        legacy = dict(application)
        legacy["note"] = (
            "Legacy acquisition-automation interface only. A false `connected` here does not "
            "disable live document processing; use live_processing_available instead."
        )
        result["legacy_acquisition_automation"] = legacy
        del result["application"]
    result["live_processing_supported"] = bool(
        result.get("native_live_document_bridge_verified")
        and result.get("native_document_lifecycle_verified")
    )
    # A process that exists is not an interface that answers. The native layer
    # reports `running` from the process list and `responding` from a window
    # message probe; both are kept here, and an explicit false always wins.
    result["application_running"] = isinstance(application, dict) and application.get("running") is True
    result["application_responding"] = (
        None if not isinstance(application, dict) else application.get("responding")
    )
    responding = result["application_responding"]
    if not result["application_running"]:
        # No process: answering state is moot, whatever the payload said.
        result["application_response_state"] = "not_running"
    elif responding is True:
        result["application_response_state"] = "responding"
    elif responding is False:
        # An explicit false from the window probe is a measured answer.
        result["application_response_state"] = "not_responding"
    else:
        # Missing or non-boolean answering state is unknown, never assumed live.
        result["application_response_state"] = "unknown"
    result["live_processing_available"] = bool(
        result["live_processing_supported"] and result["application_response_state"] == "responding"
    )
    result["native_fitting_available"] = bool(result.get("native_fit_solver_verified"))
    if result["live_processing_available"]:
        result["recommended_next_step"] = (
            "Live document tools are available. Call avantage_list_processing_documents to see open projects."
        )
    elif not result["live_processing_supported"]:
        result["recommended_next_step"] = (
            "Live document tools are unavailable. Open a .VGD or .VGP as a private session instead."
        )
    elif result["application_response_state"] == "not_running":
        result["recommended_next_step"] = (
            "Start Avantage and open the desired project, then list processing documents."
        )
    elif result["application_response_state"] == "not_responding":
        result["recommended_next_step"] = (
            "Avantage is running but not answering. Restore the application manually (close any modal "
            "dialog or wait for startup to finish), then repeat avantage_status; live document tools are "
            "not claimed available until it responds."
        )
    else:
        result["recommended_next_step"] = (
            "The running Avantage process did not report whether it is answering, so live document tools "
            "are not claimed available. Repeat avantage_status; if the answer stays unknown, use a private "
            ".VGD session or .VGP snapshot instead."
        )
    return result


def create_server(bridge: BridgeClient | None = None) -> FastMCP:
    """Create an isolated server; importing this module has no COM side effects."""
    client = bridge or BridgeClient(timeout=float(os.environ.get("AVANTAGE_TIMEOUT_SECONDS", "120")))

    @asynccontextmanager
    async def lifespan(_: FastMCP) -> AsyncIterator[dict[str, Any]]:
        try:
            yield {"bridge": client}
        finally:
            await asyncio.to_thread(client.close)

    app = FastMCP(
        "Avantage MCP",
        instructions=(
            "Control the installed Thermo Scientific Avantage spectrum processing components. "
            "Use absolute Windows paths throughout.\n"
            "\n"
            "RECOMMENDED ORDER (follow it unless the user asks for a specific step):\n"
            "1. avantage_status — read the capabilities block before anything else. It states exactly which native features this build may use.\n"
            "2. Pick a source: avantage_open_spectrum for a standalone .VGD, avantage_open_processing_project plus avantage_open_project_spectrum for a .VGP bundle.\n"
            "3. avantage_charge_shift, if and only if the user asked for an energy calibration. Do it before any background or peak work.\n"
            "4. avantage_set_background, then avantage_add_peak for each component, then avantage_set_peak_constraint.\n"
            "5. avantage_run_fit, then avantage_get_fit_model and avantage_get_model_curve to inspect convergence and residuals.\n"
            "6. Persist with avantage_save_spectrum, avantage_export_peak_table, avantage_export_model_curves or avantage_save_project_model.\n"
            "\n"
            "EDITING AN ALREADY-OPEN PROJECT: avantage_list_processing_documents, avantage_list_document_spectra, "
            "avantage_attach_document_spectrum, edit the existing peak parameters or constraints, then avantage_apply_fit_to_document. "
            "Reattach after every save. Live apply keeps the existing peak count, background and constraint attributes; adding, removing or "
            "replacing those requires the private project flow (avantage_save_project_model) with the original document closed.\n"
            "\n"
            "CHECK BEFORE CLAIMING SUCCESS: avantage_status capabilities must contain native_peak_fit before avantage_run_fit, and "
            "native_vgp_save_model before avantage_save_project_model. A fit that reached max_iterations returns converged=false. "
            "Transport success never proves a scientifically valid fit; report convergence, stop_reason and residual metrics together.\n"
            "\n"
            "HOUSEKEEPING: private working copies accumulate under the session root. Call avantage_workspace_usage to see them and "
            "avantage_prune_workspace with dry_run=true first. avantage_list_backups enumerates recovery material from earlier saves.\n"
            "\n"
            "Automatic opening and closing of application documents remain disabled. "
            "Never infer scientific fit validity from transport success."
        ),
        lifespan=lifespan,
        log_level="WARNING",
    )

    async def invoke(operation: str, **arguments: Any) -> Any:
        try:
            return await asyncio.to_thread(client.call, operation, arguments)
        except BridgeError as exc:
            details = "" if exc.details is None else " Details: " + json.dumps(exc.details, ensure_ascii=False, default=str)
            raise ToolError(with_hint(exc.code, f"{exc.code}: {exc.message}{details}")) from exc

    async def active_working_paths() -> list[str]:
        listed = await invoke("list_sessions")
        if not isinstance(listed, dict) or not isinstance(listed.get("sessions"), list):
            raise ToolError("WORKSPACE_ACTIVITY_UNKNOWN: Cannot verify active sessions; cleanup was not started.")
        paths = []
        for row in listed["sessions"]:
            if not isinstance(row, dict) or not isinstance(row.get("working_path"), str) or not row["working_path"]:
                raise ToolError("WORKSPACE_ACTIVITY_UNKNOWN: Invalid active session; cleanup was not started.")
            paths.append(row["working_path"])
        return paths

    def fail(exc: BridgeError) -> ToolError:
        details = "" if exc.details is None else " Details: " + json.dumps(exc.details, ensure_ascii=False, default=str)
        return ToolError(with_hint(exc.code, str(exc) + details))

    async def invoke_live(operation: str, **arguments: Any) -> Any:
        # Do not turn unverified native application entry points into public,
        # callable tools merely because their experimental routes exist.
        status = await invoke("status")
        if not isinstance(status, dict) or status.get("native_live_document_bridge_verified") is not True:
            raise ToolError(
                "LIVE_DOCUMENT_BRIDGE_NOT_VERIFIED: Live document integration is not enabled for this build. "
                "Use a private VGP project or VGD session. No live document operation was sent."
            )
        return await invoke(operation, **arguments)

    @app.tool(annotations=READ)
    async def avantage_status() -> dict[str, Any]:
        """Check native bridge, installed Avantage components, and application connection status.

        Read this first. `live_processing_available` is the flag that decides
        whether the live document tools work; `application.connected` only
        reports the legacy acquisition-automation interface and is routinely
        false while live processing is fully available.
        `live_processing_available` also requires the Avantage process to be
        running *and* answering: `application_response_state` is one of
        `responding`, `not_responding`, `unknown` or `not_running`, and
        `recommended_next_step` explains what to do in each case.
        """

        status = await invoke("status")
        if isinstance(status, dict):
            status = decorate_status(status)
        return status

    @app.tool(annotations=READ)
    async def avantage_application_status() -> dict[str, Any]:
        """Read running Avantage application and experiment state through its native interface."""
        return await invoke("application_status")

    @app.tool(annotations=SESSION)
    async def avantage_show_data_file(path: FilePath) -> dict[str, Any]:
        """Request that Avantage open a saved native .VGD file in its visible application.

        This sends a file-open request to Avantage. request_accepted means the
        request was sent; display_verified remains false because the application
        interface cannot verify the visible processing grid. This does not
        synchronize an in-memory MCP session with the currently selected grid.
        """
        return await invoke("show_data_file", path=path)

    @app.tool(annotations=READ)
    async def avantage_list_sample_files() -> dict[str, Any]:
        """Find installed Avantage example spectra suitable for testing a processing workflow."""
        return await invoke("list_sample_files")

    @app.tool(annotations=SESSION)
    async def avantage_open_spectrum(path: FilePath) -> dict[str, Any]:
        """Load an Avantage native spectrum into an independent editable session and return its id.

        Pass a native spectrum file, not an experiment/project directory. The source
        file is retained; explicitly save to a destination to persist processing.
        """
        return await invoke("open_spectrum", path=path)

    @app.tool(annotations=SESSION)
    async def avantage_open_processing_project(path: FilePath) -> dict[str, Any]:
        """Read an Avantage .VGP and its adjacent matching .DATA directory into a private snapshot.

        Returns project_id and spectra with their zero-based indices. Existing
        fitted peaks, backgrounds and constraints can be opened for editing via
        avantage_open_project_spectrum. The original project is protected.
        This does not attach to or alter an open Avantage window.
        """
        return await invoke("open_processing_project", path=path)

    @app.tool(annotations=READ)
    async def avantage_list_project_spectra(project_id: ProjectId) -> dict[str, Any]:
        """List names, source references, point counts and fitted-peak counts in a private VGP snapshot."""
        return await invoke("list_project_spectra", project_id=project_id)

    @app.tool(annotations=SESSION)
    async def avantage_open_project_spectrum(
        project_id: ProjectId, spectrum_index: SpectrumIndex,
    ) -> dict[str, Any]:
        """Create an editable session for one VGP spectrum, preserving native data and existing model.

        Use an index from avantage_list_project_spectra. Returns session_id plus
        imported background, peak parameters and constraints. A session edit
        does not update the source VGP or an open application document.
        """
        return await invoke("open_project_spectrum", project_id=project_id, spectrum_index=spectrum_index)

    @app.tool(annotations=WRITE)
    async def avantage_close_processing_project(project_id: ProjectId) -> dict[str, Any]:
        """Release a private VGP snapshot. Independently opened spectrum sessions remain editable."""
        return await invoke("close_processing_project", project_id=project_id)

    @app.tool(annotations=WRITE)
    async def avantage_save_project_model(session_id: SessionId, path: FilePath | None = None) -> dict[str, Any]:
        """Save a private project spectrum's edited model to its original VGP by default.

        Requires a session opened by avantage_open_project_spectrum and an open
        source project. Omit path to back up and replace the complete original
        bundle; that original must be closed in Avantage. An explicit path
        requests a new copy and must not exist. No application windows open.
        Requires native_project_save_verified=true and full native verification.
        """
        status = await invoke("status")
        if not isinstance(status, dict) or status.get("native_project_save_verified") is not True:
            raise ToolError(
                "PROJECT_SAVE_NOT_VERIFIED: Complete VGP model saving is not enabled for this build. "
                "No project write was sent."
            )
        if path is not None:
            return await invoke("save_project_model", session_id=session_id, path=path)
        try:
            return await asyncio.to_thread(save_project_model_in_place, client, session_id)
        except BridgeError as exc:
            raise fail(exc) from exc
        except (ValueError, OSError) as exc:
            raise ToolError(str(exc)) from exc

    @app.tool(annotations=READ)
    async def avantage_list_processing_documents() -> dict[str, Any]:
        """List documents in the running Avantage application, when live integration is verified.

        Document ids belong to this worker. Relist after a worker restart. A
        build without native_live_document_bridge_verified=true rejects this
        request before invoking the experimental live interface.
        """
        return await invoke_live("list_processing_documents")

    @app.tool(annotations=READ)
    async def avantage_list_document_spectra(document_id: DocumentId) -> dict[str, Any]:
        """List spectra and native fitted-peak counts in a live Avantage document.

        Requires verified live integration. Choose the spectrum_index from this
        result when attaching, rather than inferring the currently selected grid.
        `active_in_grid` marks the spectra currently drawn in the visible
        processing grid; it is a display flag, not a filter on what can be
        attached. `visible_in_grid` is kept as an alias for compatibility.
        """
        result = await invoke_live("list_document_spectra", document_id=document_id)
        if isinstance(result, dict) and isinstance(result.get("spectra"), list):
            active = []
            for row in result["spectra"]:
                if isinstance(row, dict) and "visible_in_grid" in row:
                    row["active_in_grid"] = row["visible_in_grid"]
                    if row["visible_in_grid"]:
                        active.append(
                            {"spectrum_index": row.get("spectrum_index"), "name": row.get("name")}
                        )
            result["active_spectra"] = active
            result["active_note"] = (
                "active_in_grid marks spectra drawn in the visible grid. Any index in "
                "`spectra` can be attached; attaching does not change what the user sees."
            )
        return result

    @app.tool(annotations=SESSION)
    async def avantage_open_processing_document(path: FilePath) -> dict[str, Any]:
        """Disabled: manual original-file opening is required after a lifecycle failure."""
        raise ToolError("LIVE_OPEN_NOT_VERIFIED: Open the original manually in Avantage. No application call was sent.")

    @app.tool(annotations=SESSION)
    async def avantage_attach_document_spectrum(
        document_id: DocumentId, spectrum_index: SpectrumIndex,
    ) -> dict[str, Any]:
        """Snapshot one live spectrum and its existing native peak model for conversational edits.

        Requires verified live integration. Returns session_id with a live link;
        edits stay private until avantage_apply_fit_to_document succeeds. Use
        this attachment rather than a separate VGP/VGD session for live apply.
        The response echoes which document, index and spectrum name was attached
        so a mistaken index is visible before any edit is made.
        """
        result = await invoke_live("attach_document_spectrum", document_id=document_id, spectrum_index=spectrum_index)
        if isinstance(result, dict):
            # Derive the echo from the attach payload alone: an extra native
            # round-trip here would double every attachment's cost.
            session = result.get("session") if isinstance(result.get("session"), dict) else {}
            spectrum = result.get("spectrum") if isinstance(result.get("spectrum"), dict) else {}
            source_path = session.get("source_path") or spectrum.get("source_path")
            name = None
            if isinstance(source_path, str) and source_path:
                name = source_path.replace("\\", "/").rsplit("/", 1)[-1]
                if name.lower().endswith(".vgd"):
                    name = name[:-4]
            model = result.get("model") if isinstance(result.get("model"), dict) else {}
            peaks = model.get("peaks")
            result["attached"] = {
                "document_id": document_id,
                "spectrum_index": spectrum_index,
                "spectrum_name": name,
                "source_path": source_path,
                "point_count": session.get("point_count"),
                "existing_peak_count": len(peaks) if isinstance(peaks, list) else 0,
            }
            result["reminder"] = (
                "Edits stay private until avantage_apply_fit_to_document. Reattach after saving."
            )
        return result

    @app.tool(annotations=WRITE)
    async def avantage_apply_fit_to_document(session_id: SessionId, save_to_original: bool = True) -> dict[str, Any]:
        """Apply an attached session's peak model to its corresponding live Avantage spectrum.

        Requires verified live integration and a current live attachment. An
        edited private model alone does not prove successful application. Check
        the returned native readback verification before reporting success.
        By default, saves back to the attached original VGP with a complete
        verified backup and retains the same existing window. Live apply requires
        the existing peak count, background and constraint attributes; parameter
        values and links may change. Unsupported structural edits are rejected
        before mutation. Reattach after saving. save_to_original=false updates
        that same live window without saving the source file.
        """
        try:
            return await asyncio.to_thread(apply_document_model, client, session_id, save_to_original)
        except BridgeError as exc:
            raise fail(exc) from exc
        except (ValueError, OSError) as exc:
            raise ToolError(str(exc)) from exc

    @app.tool(annotations=WRITE)
    async def avantage_save_processing_document(
        document_id: DocumentId, path: FilePath,
    ) -> dict[str, Any]:
        """Back up and save this existing window to its own original path.

        Same verified operation as save_processing_document_in_place; no Save As.
        """
        return await avantage_save_processing_document_in_place(document_id, path)

    @app.tool(annotations=READ)
    async def avantage_list_sessions() -> dict[str, Any]:
        """List spectra currently loaded in this MCP worker and their session ids."""
        return await invoke("list_sessions")

    @app.tool(annotations=WRITE)
    async def avantage_save_processing_document_in_place(
        document_id: DocumentId, path: FilePath,
    ) -> dict[str, Any]:
        """Back up the full VGP+DATA, use ordinary Save and verify the saved project.

        The target must be the active original document. Its window stays open.
        No automatic retry follows an uncertain save. Reattach after saving.
        """
        try:
            return await asyncio.to_thread(save_document_in_place, client, document_id, path)
        except BridgeError as exc:
            raise fail(exc) from exc
        except (ValueError, OSError) as exc:
            raise ToolError(str(exc)) from exc

    @app.tool(annotations=WRITE)
    async def avantage_close_session(session_id: SessionId) -> dict[str, Any]:
        """Release a spectrum session. Unsaved in-memory changes are discarded."""
        return await invoke("close_session", session_id=session_id)

    @app.tool(annotations=READ)
    async def avantage_get_spectrum_info(session_id: SessionId) -> dict[str, Any]:
        """Read native spectrum metadata, point count, axes, and available processing state."""
        return await invoke("get_spectrum_info", session_id=session_id)

    @app.tool(annotations=READ)
    async def avantage_get_spectrum_data(
        session_id: SessionId, offset: Offset = 0, limit: PageSize = 1000
    ) -> dict[str, Any]:
        """Read a bounded page of spectrum points. Iterate offset for larger spectra."""
        return await invoke("get_spectrum_data", session_id=session_id, offset=offset, limit=limit)

    @app.tool(annotations=SESSION)
    async def avantage_charge_shift(session_id: SessionId, delta_ev: EnergyShift) -> dict[str, Any]:
        """Apply a signed binding-energy calibration shift to this spectrum session.

        Positive delta_ev increases binding energy; the native kinetic-energy
        axis shifts by the opposite amount. Apply before configuring a background
        or peak model; existing models are rejected to prevent stale coordinates.
        Save explicitly to persist the calibrated spectrum.
        """
        return await invoke("charge_shift", session_id=session_id, delta_ev=delta_ev)

    @app.tool(annotations=WRITE)
    async def avantage_set_background(
        session_id: SessionId,
        kind: BackgroundKind,
        start_binding_ev: Finite | None = None,
        end_binding_ev: Finite | None = None,
        points_to_average: EndpointAverage = 2,
        shirley_iterations: ShirleyIterations = 100,
        shirley_convergence: Positive = 1.0,
        shirley_smooth: bool = False,
        tougaard_type: TougaardType = "bc",
        tougaard_b: Nonnegative = 2866,
        tougaard_c: Positive = 1643,
        tougaard_d: Nonnegative = 0,
        tougaard_y_offset: Finite = 0,
        tougaard_energy_correction: bool = False,
    ) -> dict[str, Any]:
        """Calculate a native Avantage background for a selected binding-energy range.

        Choose linear, shirley, smart, tougaard, or simple. Omitted endpoints
        default to the spectrum's minimum and maximum binding energy. Supply
        increasing endpoints inside the spectrum and enough points for averaging.
        Shirley settings apply only to kind='shirley'; Tougaard settings apply
        only to kind='tougaard'. Replacing the background preserves existing
        peaks, parameters and constraints; the previous fit report is cleared.
        Original intensities are retained; subtraction is returned separately.
        """
        args = {"session_id": session_id, "kind": kind,
                "points_to_average": points_to_average,
                "shirley_iterations": shirley_iterations,
                "shirley_convergence": shirley_convergence,
                "shirley_smooth": shirley_smooth,
                "tougaard_type": tougaard_type, "tougaard_b": tougaard_b,
                "tougaard_c": tougaard_c, "tougaard_d": tougaard_d,
                "tougaard_y_offset": tougaard_y_offset,
                "tougaard_energy_correction": tougaard_energy_correction}
        if start_binding_ev is not None:
            args["start_binding_ev"] = start_binding_ev
        if end_binding_ev is not None:
            args["end_binding_ev"] = end_binding_ev
        return await invoke("set_background", **args)

    @app.tool(annotations=READ)
    async def avantage_get_background(session_id: SessionId) -> dict[str, Any]:
        """Read the configured native background type, range, and parameters."""
        return await invoke("get_background", session_id=session_id)

    @app.tool(annotations=WRITE)
    async def avantage_clear_background(session_id: SessionId) -> dict[str, Any]:
        """Remove the background after clearing any peak model that depends on it.

        Clear both peak model and background before applying a new energy shift.
        """
        return await invoke("clear_background", session_id=session_id)

    @app.tool(annotations=READ)
    async def avantage_get_background_data(
        session_id: SessionId, offset: Offset = 0, limit: PageSize = 1000
    ) -> dict[str, Any]:
        """Read background and background-subtracted intensities in bounded pages.

        Points outside a restricted background range are null, not zero.
        The original spectrum is unchanged by this read.
        """
        return await invoke("get_background_data", session_id=session_id, offset=offset, limit=limit)

    @app.tool(annotations=SESSION)
    async def avantage_add_peak(session_id: SessionId, peak: PeakSpec) -> dict[str, Any]:
        """Create a real Avantage native peak object with explicit initial parameters.

        centre_ev defaults to binding energy. gaussian_lorentz is a 0..1 native
        parameter; gl_combine_method selects product, sum, or convolution.
        fwhm_ev must be positive. This configures a model without
        executing optimization. The model is independent of the visible UI;
        saving a spectrum alone does not embed this peak model into its VGD file.
        """
        return await invoke("add_peak", session_id=session_id, peak=peak.model_dump())

    @app.tool(annotations=WRITE)
    async def avantage_update_peak(
        session_id: SessionId, peak_id: PeakId, patch: PeakPatch
    ) -> dict[str, Any]:
        """Update supplied parameters of an existing native peak; keep omitted fields.

        A centre update uses binding energy unless energy_scale is specified.
        This changes model parameters without running a fit.
        """
        return await invoke("update_peak", session_id=session_id, peak_id=peak_id, patch=patch.model_dump(exclude_unset=True))

    @app.tool(annotations=WRITE)
    async def avantage_remove_peak(session_id: SessionId, peak_id: PeakId) -> dict[str, Any]:
        """Remove a peak; first remove other peaks' constraints that refer to it."""
        return await invoke("remove_peak", session_id=session_id, peak_id=peak_id)

    @app.tool(annotations=READ)
    async def avantage_get_fit_model(session_id: SessionId) -> dict[str, Any]:
        """Read back native peak parameters and constraints, including optimization status.

        A configured model is not an optimized fit. Check fit_executed explicitly.
        """
        return await invoke("get_fit_model", session_id=session_id)

    @app.tool(annotations=WRITE)
    async def avantage_run_fit(
        session_id: SessionId, max_iterations: FitIterations = 1000,
        tolerance: Positive = 0.0001, asymmetric: bool = False,
        timeout_seconds: FitTimeout = 90,
    ) -> dict[str, Any]:
        """Optimize the current model with Avantage's native simplex fitting engine.

        Set a background, add peaks with initial parameters, then configure constraints.
        All peaks must use the same G/L combination method. Each fit runs in an
        isolated native child; failures retain the current spectrum and model.
        Peak ids remain stable after fitting. Check converged/stop_reason and
        residual metrics; reaching the iteration limit is not convergence.
        asymmetric=False fits symmetric G/L shapes; True enables native tails.
        Keep the bridge and MCP client timeout longer than timeout_seconds.
        Save as a complete VGP or export the fitted peak table as VGT; VGD contains the spectrum.
        """
        return await invoke("run_fit", session_id=session_id, max_iterations=max_iterations,
                            tolerance=tolerance, asymmetric=asymmetric, timeout_seconds=timeout_seconds)

    @app.tool(annotations=WRITE)
    async def avantage_export_peak_table(
        session_id: SessionId, path: FilePath, overwrite: bool = False,
    ) -> dict[str, Any]:
        """Save native peaks, backgrounds and constraints in an Avantage .VGT file.

        An independent native reload verifies every parameter before publishing.
        Requires a background and at least one peak. Save matching VGD separately.
        This table is directly readable by Avantage's peak-table loading function.
        """
        return await invoke("export_peak_table", session_id=session_id, path=path, overwrite=overwrite)

    @app.tool(annotations=READ)
    async def avantage_inspect_peak_table(session_id: SessionId, path: FilePath) -> dict[str, Any]:
        """Read an Avantage .VGT with the session's spectrum without changing its model.

        Returns native kinetic-energy centres, backgrounds and constraint references.
        Use the matching VGD spectrum; the native loader associates peaks with it.
        """
        return await invoke("inspect_peak_table", session_id=session_id, path=path)

    @app.tool(annotations=WRITE)
    async def avantage_import_peak_table(session_id: SessionId, path: FilePath) -> dict[str, Any]:
        """Load native Avantage .VGT peaks, background and constraints into this session.

        Atomically replaces the current model. Supports up to 100 peaks sharing
        one background within this spectrum. Native objects and linked constraints
        are retained. Peak ids are new. Loading a table does not execute fitting;
        VGT has no verifiable optimizer execution report. Failed imports retain
        the current model. Use the matching VGD spectrum.
        """
        return await invoke("import_peak_table", session_id=session_id, path=path)

    @app.tool(annotations=WRITE)
    async def avantage_clear_fit_model(session_id: SessionId) -> dict[str, Any]:
        """Discard the session's peak model and its constraints."""
        return await invoke("clear_fit_model", session_id=session_id)

    @app.tool(annotations=WRITE)
    async def avantage_set_peak_constraint(
        session_id: SessionId, peak_id: PeakId, attribute: PeakAttribute, constraint: ConstraintSpec
    ) -> dict[str, Any]:
        """Set a real Avantage peak constraint and read back its native parameters.

        For range constraints value1/value2 are lower/upper bounds. Linked
        constraints require reference_peak_id and an operator. Centre values
        use energy_scale; binding-energy centre links support plus/minus only.
        Native validation rejects circular references and invalid operations.
        This configures a constraint without executing optimization.
        """
        return await invoke("set_peak_constraint", session_id=session_id, peak_id=peak_id,
                            attribute=attribute, constraint=constraint.model_dump(exclude_none=True))

    @app.tool(annotations=WRITE)
    async def avantage_remove_peak_constraint(
        session_id: SessionId, peak_id: PeakId, attribute: PeakAttribute
    ) -> dict[str, Any]:
        """Remove one native peak constraint and return the remaining peak state."""
        return await invoke("remove_peak_constraint", session_id=session_id, peak_id=peak_id, attribute=attribute)

    @app.tool(annotations=READ)
    async def avantage_get_model_curve(
        session_id: SessionId, offset: Offset = 0, limit: PageSize = 1000, asymmetric: bool = False
    ) -> dict[str, Any]:
        """Evaluate current parameters with native Avantage peak shapes and return a page.

        Includes component curves, peak sum, total model, residual, RSS and RMSE.
        No optimization is performed. Metrics cover the full valid model region,
        independently of pagination. With a restricted background range, total
        model and residual are null outside it. asymmetric selects native
        asymmetric shapes; false requests native symmetric shapes.
        """
        return await invoke("get_model_curve", session_id=session_id, offset=offset, limit=limit, asymmetric=asymmetric)

    @app.tool(annotations=WRITE)
    async def avantage_export_fit_model(
        session_id: SessionId, path: FilePath, overwrite: bool = False
    ) -> dict[str, Any]:
        """Save background, peak parameters and constraints as an Avantage MCP JSON recipe.

        This recipe can be reimported by this MCP server. It is not a native
        Avantage VGD/VGT file. Includes a fingerprint of spectrum axes/intensities.
        """
        return await invoke("export_fit_model", session_id=session_id, path=path, overwrite=overwrite)

    @app.tool(annotations=WRITE)
    async def avantage_import_fit_model(
        session_id: SessionId, path: FilePath, allow_different_spectrum: bool = False
    ) -> dict[str, Any]:
        """Restore an exported JSON model recipe, atomically replacing this session's model.

        The spectrum fingerprint must match unless parameter reuse on different
        data is explicitly requested. Peak ids and linked references are remapped.
        A failed import preserves the previous model. No optimization is run.
        """
        return await invoke("import_fit_model", session_id=session_id, path=path, allow_different_spectrum=allow_different_spectrum)

    @app.tool(annotations=WRITE)
    async def avantage_export_model_curves(
        session_id: SessionId, path: FilePath, format: ExportFormat = "csv",
        overwrite: bool = False, asymmetric: bool = False,
    ) -> dict[str, Any]:
        """Export native component curves, total model, residuals and data to CSV or JSON.

        Evaluates the current parameters without optimizing them. Existing files
        are protected unless overwrite is explicit. Honor valid_model_region in
        JSON; CSV leaves total/residual fields blank outside that region.
        """
        return await invoke("export_model_curves", session_id=session_id, path=path, format=format,
                            overwrite=overwrite, asymmetric=asymmetric)

    @app.tool(annotations=WRITE)
    async def avantage_save_spectrum(
        session_id: SessionId, path: FilePath, overwrite: bool = False
    ) -> dict[str, Any]:
        """Save the current native spectrum to an absolute destination path.

        Existing files are rejected unless overwrite is explicitly true. Preserve
        the original by using a new destination name for processed results.
        This saves spectrum data and calibrated axes. Independently configured
        background and peak models are not embedded in the VGD file.
        If the source was moved or is unavailable, a successful save returns
        source_unchanged=null and source_check_error; null does not mean unchanged.
        """
        return await invoke("save_spectrum", session_id=session_id, path=path, overwrite=overwrite)

    @app.tool(annotations=WRITE)
    async def avantage_export_spectrum(
        session_id: SessionId, path: FilePath, format: ExportFormat = "csv", overwrite: bool = False
    ) -> dict[str, Any]:
        """Export raw intensities and energy axes to CSV/JSON with overwrite protection."""
        return await invoke("export_spectrum", session_id=session_id, path=path, format=format, overwrite=overwrite)

    @app.tool(annotations=READ)
    async def avantage_workspace_usage(stale_hours: StaleHours = 72.0) -> dict[str, Any]:
        """Report private working-copy usage under the session root.

        Every opened or attached spectrum is copied into its own folder that
        survives close_session. Use this before pruning, and to explain disk
        usage. Native leases protect sessions and projects across workers;
        legacy folders without a lease are reported as unverified and kept.
        `stale_folders` and `stale_bytes` show what avantage_prune_workspace
        would reclaim at the same `stale_hours`, without modifying anything.
        """
        active = await active_working_paths()
        usage = workspace.workspace_usage(keep_paths=active, stale_hours=stale_hours)
        usage["active_session_count"] = len(active)
        return usage

    @app.tool(annotations=HOUSEKEEPING)
    async def avantage_prune_workspace(
        older_than_hours: StaleHours = 72.0,
        dry_run: bool = True,
        max_delete: PageLimit = 400,
    ) -> dict[str, Any]:
        """Delete stale private working-copy folders to reclaim disk space.

        A folder is eligible only when its newest file is older than
        older_than_hours and has an unlocked native lease. Live sessions and
        projects across workers and unmarked legacy folders are kept.
        Defaults to dry_run=true, which reports what would
        be removed without touching anything. This never touches the user's
        source spectra, VGP bundles or recovery backups.
        """
        active = await active_working_paths()
        return await asyncio.to_thread(
            workspace.prune_workspace,
            older_than_hours=older_than_hours,
            keep_paths=active,
            dry_run=dry_run,
            max_delete=max_delete,
        )

    @app.tool(annotations=READ)
    async def avantage_list_backups(limit: PageLimit = 200) -> dict[str, Any]:
        """Enumerate recovery material written by earlier save and writeback calls.

        Recovery folders live outside the sample directory on the same volume,
        so they are easy to lose track of. Each entry reports the transaction
        kind, journal status, target original and whether a backup is present.
        """
        return await asyncio.to_thread(workspace.list_backups, limit=limit)

    @app.tool(annotations=HOUSEKEEPING)
    async def avantage_prune_backups(
        older_than_days: BackupDays = 30.0,
        dry_run: bool = True,
        confirm: bool = False,
    ) -> dict[str, Any]:
        """Delete finished recovery folders older than older_than_days.

        Only journals whose status is committed or rolled_back are eligible;
        an inspection_required or recovery_required journal is never removed.
        Requires dry_run=false and confirm=true together, and defaults to a
        read-only preview.
        """
        return await asyncio.to_thread(
            workspace.prune_backups,
            older_than_days=older_than_days,
            dry_run=dry_run,
            confirm=confirm,
        )

    return app


def main() -> None:
    parser = argparse.ArgumentParser(description="Local Thermo Scientific Avantage MCP server (stdio)")
    parser.add_argument("--bridge", help="Native AvantageBridge.exe path; defaults to AVANTAGE_BRIDGE_PATH")
    parser.add_argument("--timeout", type=float, default=float(os.environ.get("AVANTAGE_TIMEOUT_SECONDS", "120")), help="Per-operation native response timeout in seconds (default: 120)")
    options = parser.parse_args()
    bridge = BridgeClient(options.bridge, timeout=options.timeout)
    try:
        create_server(bridge).run(transport="stdio")
    finally:
        bridge.close()


if __name__ == "__main__":
    main()
