"""Stable public surface: tool count, annotations, and instructions coverage."""

import asyncio

import pytest

import avantage_mcp.server as module

#: Bumped deliberately whenever a tool is added or removed. The live integration
#: tests and the writeback tests import this so the number lives in one place.
EXPECTED_TOOL_COUNT = 47

HOUSEKEEPING_TOOLS = {
    "avantage_prune_workspace",
    "avantage_prune_backups",
}
READ_ONLY_TOOLS = {
    "avantage_workspace_usage",
    "avantage_list_backups",
}


def _tools():
    async def scenario():
        server = module.create_server(object())
        return {tool.name: tool for tool in await server.list_tools()}

    return asyncio.run(scenario())


def test_tool_inventory_is_stable():
    tools = _tools()
    assert len(tools) == EXPECTED_TOOL_COUNT
    assert HOUSEKEEPING_TOOLS | READ_ONLY_TOOLS <= set(tools)


def test_housekeeping_tools_are_annotated_destructive_but_idempotent():
    tools = _tools()
    for name in HOUSEKEEPING_TOOLS:
        annotations = tools[name].annotations
        assert annotations.readOnlyHint is False, name
        assert annotations.idempotentHint is True, name
        assert annotations.openWorldHint is False, name


def test_reporting_tools_are_read_only():
    tools = _tools()
    for name in READ_ONLY_TOOLS:
        annotations = tools[name].annotations
        assert annotations.readOnlyHint is True, name
        assert annotations.destructiveHint is False, name


def test_read_only_tools_never_claim_destructive_behavior():
    tools = _tools()
    for name, tool in tools.items():
        if tool.annotations.readOnlyHint:
            assert tool.annotations.destructiveHint is False, name


def test_prune_tools_default_to_a_read_only_preview():
    tools = _tools()
    for name in ("avantage_prune_workspace", "avantage_prune_backups"):
        schema = tools[name].inputSchema["properties"]
        assert schema["dry_run"].get("default") is True, name


def test_instructions_cover_the_documented_workflow():
    instructions = module.create_server(object()).instructions
    for step in (
        "avantage_status",
        "avantage_set_background",
        "avantage_add_peak",
        "avantage_run_fit",
        "avantage_apply_fit_to_document",
        "avantage_prune_workspace",
    ):
        assert step in instructions, step
    assert "converged=false" in instructions


@pytest.mark.parametrize(
    "code",
    [
        "SESSION_NOT_FOUND",
        "LIVE_OPERATION_BUSY",
        "BACKGROUND_REQUIRED",
        "ORIGINAL_IN_USE",
        "BRIDGE_TIMEOUT",
    ],
)
def test_known_error_codes_carry_a_remediation(code):
    message = module.with_hint(code, f"{code}: something failed")
    assert "Next: " in message


def test_unknown_error_codes_are_left_untouched():
    assert module.with_hint("SOME_NEW_CODE", "plain") == "plain"


def test_status_decoration_separates_legacy_automation_from_live_support():
    status = module.decorate_status(
        {
            "server_version": "9.9.9",
            "capabilities": ["native_peak_fit", "live_document_read"],
            "native_fit_solver_verified": True,
            "native_live_document_bridge_verified": True,
            "native_document_lifecycle_verified": True,
            "application": {"running": True, "responding": True, "connected": False,
                            "executable": "C:\\Avantage\\Avantage.exe"},
        }
    )
    assert "application" not in status
    assert status["legacy_acquisition_automation"]["connected"] is False
    # The raw native application payload is preserved, not just summarized.
    assert status["legacy_acquisition_automation"]["executable"] == "C:\\Avantage\\Avantage.exe"
    assert status["application_running"] is True
    assert status["application_responding"] is True
    assert status["application_response_state"] == "responding"
    assert status["live_processing_available"] is True
    assert status["native_fitting_available"] is True
    assert status["server_version"] == module.__version__
    assert status["native_bridge_version"] == "9.9.9"
    assert status["capability_meaning"]["native_peak_fit"] == "avantage_run_fit works"


def test_status_reports_live_unavailable_when_the_lifecycle_flag_is_missing():
    status = module.decorate_status(
        {
            "capabilities": [],
            "native_fit_solver_verified": False,
            "native_live_document_bridge_verified": True,
            "native_document_lifecycle_verified": False,
            "application": {"running": True, "responding": True},
        }
    )
    assert status["live_processing_available"] is False
    assert status["application_response_state"] == "responding"
    assert "unavailable" in status["recommended_next_step"]


@pytest.mark.parametrize("application", [{"running": False}, {}, None])
def test_supported_bridge_does_not_claim_an_inactive_application_is_available(application):
    status = module.decorate_status({
        "native_live_document_bridge_verified": True,
        "native_document_lifecycle_verified": True,
        "application": application,
    })
    assert status["live_processing_supported"] is True
    assert status["live_processing_available"] is False
    assert status["application_running"] is False
    assert status["application_response_state"] == "not_running"
    assert "Start Avantage" in status["recommended_next_step"]


@pytest.mark.parametrize(
    ("application", "expected_state", "available", "step_fragment"),
    [
        # Answered and running: the only state that unlocks live document tools.
        ({"running": True, "responding": True}, "responding", True, "avantage_list_processing_documents"),
        # Process exists but its window does not answer.
        ({"running": True, "responding": False}, "not_responding", False, "not answering"),
        # Native could not measure the window: unknown must not be read as live.
        ({"running": True, "responding": None}, "unknown", False, "did not report whether it is answering"),
        ({"running": True}, "unknown", False, "did not report whether it is answering"),
        # Non-boolean answering values are unknown, never assumed live.
        ({"running": True, "responding": "yes"}, "unknown", False, "did not report whether it is answering"),
        # Not running at all.
        ({"running": False, "responding": None}, "not_running", False, "Start Avantage"),
    ],
)
def test_live_availability_requires_a_running_and_responding_application(
    application, expected_state, available, step_fragment
):
    status = module.decorate_status({
        "native_live_document_bridge_verified": True,
        "native_document_lifecycle_verified": True,
        "application": application,
    })
    assert status["live_processing_supported"] is True
    assert status["application_response_state"] == expected_state
    assert status["application_running"] is (application.get("running") is True)
    assert status["application_responding"] == application.get("responding")
    assert status["live_processing_available"] is available
    assert step_fragment in status["recommended_next_step"]
    if not available and status["application_running"]:
        assert "Start Avantage" not in status["recommended_next_step"]


@pytest.mark.parametrize("application", [{"running": True, "responding": False},
                                         {"running": True},
                                         {"running": False}])
def test_unverified_capability_never_claims_live_availability(application):
    status = module.decorate_status({
        "native_live_document_bridge_verified": False,
        "native_document_lifecycle_verified": True,
        "application": application,
    })
    assert status["live_processing_supported"] is False
    assert status["live_processing_available"] is False
    assert "unavailable" in status["recommended_next_step"]
