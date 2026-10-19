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

from .bridge_client import BridgeClient, BridgeError
from .project_writeback import apply_document_model, save_document_in_place, save_project_model_in_place
from .schemas import (
    BackgroundKind, ConstraintSpec, EndpointAverage, EnergyShift, ExportFormat,
    DocumentId, FilePath, Finite, FitIterations, FitTimeout, Nonnegative, Offset, PageSize, PeakAttribute, PeakId, PeakPatch, PeakSpec,
    Positive, ProjectId, SessionId, ShirleyIterations, SpectrumIndex, TougaardType,
)


READ = ToolAnnotations(readOnlyHint=True, destructiveHint=False, idempotentHint=True, openWorldHint=False)
SESSION = ToolAnnotations(readOnlyHint=False, destructiveHint=False, idempotentHint=False, openWorldHint=False)
WRITE = ToolAnnotations(readOnlyHint=False, destructiveHint=True, idempotentHint=False, openWorldHint=False)


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
            "Use avantage_status first. For VGP, open a processing project, list spectra and open a selected spectrum; for VGD, open a spectrum directly. "
            "Save private VGP model edits to the original path by omitting the save path, with mandatory complete backups and no new application windows. Close that original document before replacing its bundle. "
            "For an already-open original, attach its spectrum, edit existing peak parameters/constraints, then apply_fit_to_document; by default this saves with a full backup and retains that same window. Reattach after saving. Automatic opening and closing remain disabled. "
            "Use absolute Windows paths. After a bridge restart, reopen projects and spectra and relist application documents. "
            "Live document operations require explicit verified native capabilities; a private session edit alone does not update the visible application. "
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
            raise ToolError(f"{exc.code}: {exc.message}{details}") from exc

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
        """Check native bridge, installed Avantage components, and application connection status."""
        return await invoke("status")

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
            details = '' if exc.details is None else ' Details: ' + json.dumps(exc.details, ensure_ascii=False, default=str)
            raise ToolError(str(exc) + details) from exc
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
        """
        return await invoke_live("list_document_spectra", document_id=document_id)

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
        """
        return await invoke_live("attach_document_spectrum", document_id=document_id, spectrum_index=spectrum_index)

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
            details = "" if exc.details is None else " Details: " + json.dumps(exc.details, ensure_ascii=False, default=str)
            raise ToolError(str(exc) + details) from exc
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
            details = "" if exc.details is None else " Details: " + json.dumps(exc.details, ensure_ascii=False, default=str)
            raise ToolError(str(exc) + details) from exc
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
