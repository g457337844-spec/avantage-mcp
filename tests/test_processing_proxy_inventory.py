"""Synthetic PE32+ fixtures for the MIDL object-interface inventory tool.

Every fixture is built byte by byte here. Nothing is copied from a vendor DLL,
no Avantage component is loaded, and no user data is involved: these tests only
check that the parser reads the real x64 MIDL object-interface layouts, refuses
virtual-only space, and reports ambiguity instead of guessing.

Layout notes (x64, ``/Oicf`` object interfaces), which the fixtures mirror:

* ``CInterfaceStubVtbl``: ``piid`` at +0, ``pServerInfo`` at +8,
  ``DispatchTableCount`` at +16 (u32 + 4 bytes padding), ``DispatchTable`` at
  +24.
* ``CInterfaceProxyVtbl``: ``pProxyInfo`` at +0, ``piid`` at +8, inline
  dispatch slots from +16.
* Classic NDR procedure header: ``handle_type`` +0, ``oi_flags`` +1,
  ``rpc_flags`` +2, ``opnum`` +6, ``stack_size`` +8, ``oi2_flags`` +14,
  parameter count +15.
"""

from __future__ import annotations

import importlib.util
import json
import struct
import sys
import uuid
from pathlib import Path

import pytest

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "inspect_processing_proxy.py"


def _load_tool():
    """Import the tool by path, registering it first.

    ``dataclasses`` resolves annotations through ``sys.modules[cls.__module__]``
    while the module body executes, so an unregistered module spec raises
    ``AttributeError`` during collection. Registering before ``exec_module``
    fixes that, and keeps the test importable under plain ``pytest``.
    """
    name = "inspect_processing_proxy"
    existing = sys.modules.get(name)
    if existing is not None:
        return existing
    spec = importlib.util.spec_from_file_location(name, SCRIPT)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    try:
        spec.loader.exec_module(module)
    except BaseException:
        sys.modules.pop(name, None)
        raise
    return module


tool = _load_tool()

BASE = 0x180000000
HEADERS = 0x400
TEXT_RVA, TEXT_RAW, TEXT_SIZE = 0x1000, 0x400, 0x400
#: .data is large enough that an 80-slot procedure format string still fits in
#: file-backed bytes: the real grid interface has 80 slots, and the fixture must
#: be able to model that count without falling into virtual-only space.
DATA_RVA, DATA_RAW, DATA_SIZE = 0x2000, 0x800, 0x2000
IID_RVA = 0x2000
STUB_RVA = 0x2010
PROXY_RVA = 0x2040
PROXY_INFO_RVA = 0x2070
SERVER_INFO_RVA = 0x20B0
STUB_DESC_RVA = 0x20F0
#: A second, distinct stub descriptor used to prove the association check works.
ALT_STUB_DESC_RVA = 0x21F0
SYNTAX_INFO_RVA = 0x2210
OFFSETS_RVA = 0x2100
PROC_RVA = 0x2200
DUPLICATE_STUB_RVA = 0x2B00
DISPATCH_RVA = 0x1000
ENTRY_RVA = 0x1300

SLOTS = 6
NDR64 = "71710533-beba-4937-8319-b5dbef9ccc36"
OTHER_IID = "11111111-2222-3333-4444-555555555555"
#: 29 document slots and 80 grid slots are the real-file counts; the fixtures
#: use their own small numbers so nothing is hardcoded to those values.
REAL_COUNTS = (29, 80)


class Fixture:
    """A minimal PE32+ x64 image with controllable .data backing."""

    def __init__(self, *, data_raw_size: int = DATA_SIZE, virtual_size: int = DATA_SIZE) -> None:
        self.data = bytearray(HEADERS + TEXT_SIZE + DATA_SIZE)
        self.data_raw_size = data_raw_size
        self.data_virtual_size = virtual_size
        self._headers()
        self._sections()

    def _headers(self) -> None:
        view = self.data
        view[0:2] = b"MZ"
        struct.pack_into("<I", view, 0x3C, 0x80)
        view[0x80:0x84] = b"PE\x00\x00"
        coff = 0x84
        struct.pack_into("<HH", view, coff, 0x8664, 2)
        struct.pack_into("<H", view, coff + 16, 0xF0)
        optional = coff + 20
        struct.pack_into("<H", view, optional, 0x20B)
        struct.pack_into("<Q", view, optional + 24, BASE)
        struct.pack_into("<I", view, optional + 32, 0x1000)
        struct.pack_into("<I", view, optional + 36, 0x200)
        struct.pack_into("<I", view, optional + 56, 0x4000)
        struct.pack_into("<I", view, optional + 60, HEADERS)

    def _sections(self) -> None:
        table = 0x84 + 20 + 0xF0
        self._section(table, ".text", TEXT_RVA, TEXT_SIZE, TEXT_RAW, TEXT_SIZE, 0x60000020)
        self._section(
            table + 40,
            ".data",
            DATA_RVA,
            self.data_virtual_size,
            DATA_RAW,
            self.data_raw_size,
            0xC0000040,
        )

    def _section(
        self, base: int, name: str, rva: int, vsize: int, raw: int, raw_size: int, flags: int
    ) -> None:
        struct.pack_into("<8sIIII", self.data, base, name.encode(), vsize, rva, raw_size, raw)
        struct.pack_into("<I", self.data, base + 36, flags)

    def offset(self, rva: int) -> int:
        if TEXT_RVA <= rva < TEXT_RVA + TEXT_SIZE:
            return TEXT_RAW + (rva - TEXT_RVA)
        if DATA_RVA <= rva < DATA_RVA + DATA_SIZE:
            return DATA_RAW + (rva - DATA_RVA)
        raise AssertionError(f"fixture has no backing for RVA 0x{rva:x}")

    def put(self, rva: int, payload: bytes) -> None:
        start = self.offset(rva)
        self.data[start : start + len(payload)] = payload

    def u16(self, rva: int, value: int) -> None:
        self.put(rva, struct.pack("<H", value))

    def u32(self, rva: int, value: int) -> None:
        self.put(rva, struct.pack("<I", value))

    def u64(self, rva: int, value: int) -> None:
        self.put(rva, struct.pack("<Q", value))

    def va(self, rva: int) -> int:
        return BASE + rva

    def save(self, path: Path) -> Path:
        path.write_bytes(bytes(self.data))
        return path


def _proc_header(*, opnum: int, stack: int = 0x20, params: int = 1, oi2: int = 0) -> bytes:
    """Build one classic NDR procedure header with a valid descriptor region."""
    header = bytearray(16)
    header[0] = 0x00  # handle_type: explicit handle
    header[1] = 0x00  # oi_flags
    struct.pack_into("<H", header, 2, 0x1234)  # rpc_flags
    struct.pack_into("<H", header, 6, opnum)
    struct.pack_into("<H", header, 8, stack)
    header[14] = oi2
    header[15] = params
    return bytes(header) + b"\x00" * (params * 6)


def build(
    *,
    iid: str = tool.DEFAULT_IID,
    slots: int = SLOTS,
    data_raw_size: int = DATA_SIZE,
    virtual_size: int = DATA_SIZE,
    duplicate_stub: bool = False,
    bad_count: int | None = None,
    proc_offsets: dict[int, int] | None = None,
    null_dispatch: bool = False,
    syntax_count: int = 2,
) -> Fixture:
    fixture = Fixture(data_raw_size=data_raw_size, virtual_size=virtual_size)
    fixture.put(IID_RVA, uuid.UUID(iid).bytes_le)
    iid_va = fixture.va(IID_RVA)

    # CInterfaceStubVtbl: piid, pServerInfo, DispatchTableCount (+pad),
    # DispatchTable, Reserved.
    fixture.u64(STUB_RVA + 0, iid_va)
    fixture.u64(STUB_RVA + 8, fixture.va(SERVER_INFO_RVA))
    fixture.u32(STUB_RVA + 16, bad_count if bad_count is not None else slots)
    fixture.u32(STUB_RVA + 20, 0)
    fixture.u64(STUB_RVA + 24, 0 if null_dispatch else fixture.va(DISPATCH_RVA))
    fixture.u64(STUB_RVA + 32, 0)

    # CInterfaceProxyVtbl: pProxyInfo, piid, then the inline IUnknown slots.
    fixture.u64(PROXY_RVA + 0, fixture.va(PROXY_INFO_RVA))
    fixture.u64(PROXY_RVA + 8, iid_va)
    for index in range(3):
        fixture.u64(PROXY_RVA + 16 + index * 8, fixture.va(ENTRY_RVA))

    # Dispatch table: every slot resolves to one executable address.
    for index in range(max(slots, 1)):
        fixture.u64(DISPATCH_RVA + index * 8, fixture.va(ENTRY_RVA))

    # MIDL_SERVER_INFO. A purely interpreted interface leaves DispatchTable
    # null, which is valid and must not be dereferenced. ``nCount`` at +48 is
    # the *transfer syntax* count (NDR32 + NDR64 = 2), not the method count.
    fixture.u64(SERVER_INFO_RVA + 0, fixture.va(STUB_DESC_RVA))
    fixture.u64(SERVER_INFO_RVA + 8, 0 if null_dispatch else fixture.va(DISPATCH_RVA))
    fixture.u64(SERVER_INFO_RVA + 16, fixture.va(PROC_RVA))
    fixture.u64(SERVER_INFO_RVA + 24, fixture.va(OFFSETS_RVA))
    fixture.u64(SERVER_INFO_RVA + 32, 0)
    fixture.u64(SERVER_INFO_RVA + 40, 0)
    fixture.u32(SERVER_INFO_RVA + 48, syntax_count)
    fixture.u32(SERVER_INFO_RVA + 52, 0)
    fixture.u64(SERVER_INFO_RVA + 56, 0)

    # MIDL_STUBLESS_PROXY_INFO: pStubDesc, ProcFormat, FormatStringOffset,
    # pTransferSyntax, nCount (+32, transfer syntaxes), pSyntaxInfo (+40).
    fixture.u64(PROXY_INFO_RVA + 0, fixture.va(STUB_DESC_RVA))
    fixture.u64(PROXY_INFO_RVA + 8, fixture.va(PROC_RVA))
    fixture.u64(PROXY_INFO_RVA + 16, fixture.va(OFFSETS_RVA))
    fixture.u64(PROXY_INFO_RVA + 24, 0)
    fixture.u32(PROXY_INFO_RVA + 32, syntax_count)
    fixture.u32(PROXY_INFO_RVA + 36, 0)
    # A real pointer in pSyntaxInfo: reading a count here would be a huge value.
    fixture.u64(PROXY_INFO_RVA + 40, fixture.va(SYNTAX_INFO_RVA))
    fixture.put(SYNTAX_INFO_RVA, b"\x00" * 16)

    fixture.put(STUB_DESC_RVA, b"\x00" * 16)
    fixture.put(ALT_STUB_DESC_RVA, b"\x00" * 16)

    # Procedure format string: a header per method slot (3..slots-1), laid out
    # back to back from PROC_RVA.
    cursor = 0
    offsets: dict[int, int] = {}
    for slot in range(3, slots):
        offsets[slot] = cursor
        header = _proc_header(opnum=slot - 3, stack=0x20 + slot)
        fixture.put(PROC_RVA + cursor, header)
        cursor += len(header)
    # The format-offset table is biased: entry for vtable slot ``s`` lives at
    # ``OFFSETS_RVA + s * 2``, so slots 0..2 simply have no usable entry.
    for slot in range(slots):
        value = (proc_offsets or {}).get(slot, offsets.get(slot, 0))
        fixture.u16(OFFSETS_RVA + slot * 2, value)

    if duplicate_stub:
        # A second fully valid stub header for the same IID: the tool must
        # report ambiguity instead of choosing one of the two.
        fixture.u64(DUPLICATE_STUB_RVA + 0, iid_va)
        fixture.u64(DUPLICATE_STUB_RVA + 8, fixture.va(SERVER_INFO_RVA))
        fixture.u32(DUPLICATE_STUB_RVA + 16, slots)
        fixture.u32(DUPLICATE_STUB_RVA + 20, 0)
        fixture.u64(DUPLICATE_STUB_RVA + 24, fixture.va(DISPATCH_RVA))
        fixture.u64(DUPLICATE_STUB_RVA + 32, 0)

    return fixture


@pytest.fixture()
def valid(tmp_path: Path) -> Path:
    return build().save(tmp_path / "proxy.dll")


# --- happy path -------------------------------------------------------------
def test_valid_fixture_reports_both_tables_and_slot_count(valid: Path):
    result = tool.analyse(valid)
    assert result["status"] == "ok"
    assert result["file"]["sha256"] and len(result["file"]["sha256"]) == 64
    assert result["pe"]["machine"] == "AMD64"
    # The tool reports the canonical uppercase GUID form.
    assert result["iid"]["text"] == str(uuid.UUID(tool.DEFAULT_IID)).upper()
    assert result["stub"]["status"] == "ok"
    assert result["stub"]["evidence_rva"] == STUB_RVA
    assert result["stub"]["dispatch_table_count"] == SLOTS
    assert result["stub"]["dispatch_entries_total"] == SLOTS
    assert result["stub"]["dispatch_entries_executable"] == SLOTS
    assert result["proxy"]["status"] == "ok"
    assert result["proxy"]["evidence_rva"] == PROXY_RVA
    assert result["proxy"]["transfer_syntax_count"] == 2
    assert result["server_info"]["status"] == "ok"
    assert result["proxy_info"]["status"] == "ok"
    assert result["association"]["status"] == "ok"
    assert result["ndr"]["status"] == "ok"
    assert result["ndr"]["slot_count"] == SLOTS
    assert len(result["slots"]) == SLOTS


def test_iunknown_slots_are_not_given_invented_ndr_headers(valid: Path):
    slots = tool.analyse(valid)["slots"]
    for slot in slots[:3]:
        assert slot["kind"] == "iunknown"
        assert slot["has_ndr_header"] is False
        assert slot["midl_format_offset"] is None
        assert slot["status"] == "not_described"
    for slot in slots[3:]:
        assert slot["kind"] == "method"
        assert slot["has_ndr_header"] is True


def test_ndr_header_fields_use_the_object_interface_layout(valid: Path):
    slots = tool.analyse(valid)["slots"]
    for slot in slots[3:]:
        index = slot["slot"]
        assert slot["opnum"] == index - 3
        # stack_size is at +8, not +4: the value proves the offset.
        assert slot["stack_size"] == 0x20 + index
        assert slot["rpc_flags"] == 0x1234
        assert slot["handle_type"] == 0
        assert len(slot["raw_header_hex"]) == 32
        assert slot["parameter_count"] == 1
        assert slot["parameter_count_status"] == "decoded"
        assert slot["descriptor_bytes_hex"] == "00" * 6
        # No method names are ever claimed.
        assert "name" not in slot and "method_name" not in slot


def test_slot_count_comes_from_the_stub_not_a_fixed_constant(tmp_path: Path):
    for slots in (4, 7, 12):
        path = build(slots=slots).save(tmp_path / f"slots{slots}.dll")
        result = tool.analyse(path)
        assert result["stub"]["dispatch_table_count"] == slots
        assert result["ndr"]["slot_count"] == slots
        assert len(result["slots"]) == slots
        assert result["status"] == "ok"


def test_real_file_counts_are_not_hardcoded(tmp_path: Path):
    """29 and 80 are the real counts; the tool must read whatever the stub says."""
    for slots in REAL_COUNTS:
        path = build(slots=slots).save(tmp_path / f"real{slots}.dll")
        result = tool.analyse(path)
        assert result["stub"]["dispatch_table_count"] == slots
        assert len(result["slots"]) == slots
        assert result["status"] == "ok"
    # And a count that matches neither is still honoured.
    path = build(slots=13).save(tmp_path / "other.dll")
    assert tool.analyse(path)["stub"]["dispatch_table_count"] == 13


# --- identity ---------------------------------------------------------------
def test_a_different_iid_is_not_matched(tmp_path: Path):
    path = build().save(tmp_path / "proxy.dll")
    result = tool.analyse(path, OTHER_IID)
    assert result["status"] == "not_found"
    assert result["iid"]["occurrences"] == []
    assert result["stub"]["status"] == "not_found"
    assert result["proxy"]["status"] == "not_found"
    assert result["slots"] == []


def test_guid_occurrence_and_references_carry_evidence_rvas(valid: Path):
    result = tool.analyse(valid)
    assert IID_RVA in [item["rva"] for item in result["iid"]["occurrences"]]
    reference_rvas = [item["rva"] for item in result["iid"]["references"]]
    assert STUB_RVA in reference_rvas  # stub piid field
    assert PROXY_RVA + 8 in reference_rvas  # proxy piid field


def test_stub_and_proxy_headers_are_read_at_their_real_offsets(valid: Path):
    result = tool.analyse(valid)
    # The stub header starts at its piid field (offset 0), the proxy header
    # 8 bytes before its own piid field because pProxyInfo comes first.
    assert result["stub"]["evidence_rva"] == STUB_RVA
    assert result["proxy"]["evidence_rva"] == PROXY_RVA
    assert result["proxy"]["proxy_vtbl_rva"] == PROXY_RVA + 16


# --- ambiguity --------------------------------------------------------------
def test_stub_header_is_not_accepted_as_a_second_proxy_candidate(valid: Path):
    """The stub header's three 'slots' are not executable code.

    Read as a proxy, the stub header would yield pServerInfo, the dispatch
    count and zero as its IUnknown entries. None of those is a code address, so
    it must be rejected and must not create a second valid proxy candidate.
    """
    result = tool.analyse(valid)
    assert result["proxy"]["status"] == "ok"
    valid_proxies = [item for item in result["proxy"]["candidates"] if item.get("status") == "ok"]
    assert len(valid_proxies) == 1
    assert valid_proxies[0]["evidence_rva"] == PROXY_RVA
    rejected = [
        item for item in result["proxy"]["candidates"] if item.get("piid_rva") == STUB_RVA
    ]
    assert rejected and all(item["status"] != "ok" for item in rejected)
    assert result["status"] == "ok"


def test_proxy_requires_three_executable_iunknown_slots(tmp_path: Path):
    """A proxy candidate whose IUnknown slots point at data is not a proxy."""
    fixture = build()
    # Point all three inline slots at the stub descriptor (read-only data).
    for index in range(3):
        fixture.u64(PROXY_RVA + 16 + index * 8, fixture.va(STUB_DESC_RVA))
    path = fixture.save(tmp_path / "proxy.dll")
    result = tool.analyse(path)
    assert all(item["status"] != "ok" for item in result["proxy"]["candidates"])
    assert any(
        "not executable" in str(item.get("detail", "")) for item in result["proxy"]["candidates"]
    )
    # The stub and NDR evidence are unaffected; only the proxy is unproven.
    assert result["stub"]["status"] == "ok"
    assert result["ndr"]["status"] == "ok"
    assert result["status"] == "partial"


def test_duplicate_stub_candidates_are_reported_as_ambiguous(tmp_path: Path):
    path = build(duplicate_stub=True).save(tmp_path / "proxy.dll")
    result = tool.analyse(path)
    assert result["stub"]["status"] == "ambiguous"
    assert result["stub"]["evidence_rva"] is None
    assert result["status"] == "ambiguous"
    assert len([item for item in result["stub"]["candidates"] if item.get("status") == "ok"]) == 2


def test_server_and_proxy_disagreeing_on_stub_desc_is_a_mismatch(tmp_path: Path):
    fixture = build()
    fixture.u64(PROXY_INFO_RVA + 0, fixture.va(ALT_STUB_DESC_RVA))
    path = fixture.save(tmp_path / "proxy.dll")
    result = tool.analyse(path)
    assert result["association"]["status"] == "mismatch"
    assert result["status"] == "unsupported"


def test_count_disagreement_between_stub_and_server_info_is_not_a_method_count_check(tmp_path: Path):
    """``nCount`` is a transfer-syntax count, so it must not gate the slot count."""
    fixture = build(syntax_count=2)
    # Even a different syntax count leaves the method count authoritative.
    fixture.u32(SERVER_INFO_RVA + 48, 1)
    path = fixture.save(tmp_path / "proxy.dll")
    result = tool.analyse(path)
    assert result["server_info"]["status"] == "ok"
    assert result["server_info"]["transfer_syntax_count"] == 1
    assert result["stub"]["dispatch_table_count"] == SLOTS
    assert result["ndr"]["slot_count"] == SLOTS
    assert len(result["slots"]) == SLOTS
    assert result["status"] == "ok"


def test_transfer_syntax_count_is_reported_separately_from_slot_count(valid: Path):
    result = tool.analyse(valid)
    # Both structures report 2 transfer syntaxes while the interface has 6 slots.
    assert result["server_info"]["transfer_syntax_count"] == 2
    assert result["proxy_info"]["transfer_syntax_count"] == 2
    assert result["stub"]["dispatch_table_count"] == SLOTS
    assert SLOTS != 2


def test_proxy_info_syntax_info_pointer_is_not_read_as_a_count(tmp_path: Path):
    """Offset +40 is pSyntaxInfo; reading a count there would overflow."""
    path = build().save(tmp_path / "proxy.dll")
    result = tool.analyse(path)
    # pSyntaxInfo points inside the image, so a misread would be a huge number
    # and would have produced an "implausible count" failure.
    assert result["proxy_info"]["status"] == "ok"
    assert result["proxy_info"]["transfer_syntax_count"] == 2
    assert result["proxy_info"]["syntax_info_va"] == hex(BASE + SYNTAX_INFO_RVA)
    assert result["proxy_info"]["transfer_syntax_count"] < 64


def test_an_implausible_transfer_syntax_count_is_rejected(tmp_path: Path):
    """A pointer read as a count must fail closed, not pass silently."""
    path = build(syntax_count=0).save(tmp_path / "proxy.dll")
    result = tool.analyse(path)
    assert result["server_info"]["status"] == "ambiguous"
    assert result["status"] == "ambiguous"


# --- virtual-only space -----------------------------------------------------
def test_virtual_only_rva_is_refused_rather_than_read_as_zero(tmp_path: Path):
    path = build(data_raw_size=0x200, virtual_size=DATA_SIZE).save(tmp_path / "proxy.dll")
    image = tool.PeImage(path.read_bytes())
    with pytest.raises(tool.PeError) as error:
        image.rva_to_offset(DATA_RVA + 0x400, 8)
    assert error.value.code == "out_of_bounds"
    assert "virtual-only" in str(error.value)


def test_pointer_into_virtual_only_space_is_reported_not_guessed(tmp_path: Path):
    fixture = build(data_raw_size=0x200)
    # Point the format-offset table at an RVA that only exists virtually.
    fixture.u64(SERVER_INFO_RVA + 24, fixture.va(DATA_RVA + 0x400))
    path = fixture.save(tmp_path / "proxy.dll")
    result = tool.analyse(path)
    assert result["server_info"]["status"] == "out_of_bounds"
    assert result["status"] == "unsupported"
    assert result["slots"] == []


# --- truncation and malformed offsets --------------------------------------
def test_truncated_file_is_rejected(tmp_path: Path):
    fixture = build()
    path = tmp_path / "short.dll"
    path.write_bytes(bytes(fixture.data)[:0x100])
    result = tool.analyse(path)
    assert result["status"] == "truncated"
    assert result["error"]["code"] == "truncated"


def test_section_raw_data_beyond_end_of_file_is_rejected(tmp_path: Path):
    fixture = build()
    table = 0x84 + 20 + 0xF0
    struct.pack_into("<I", fixture.data, table + 40 + 20, 0xFFFF0000)
    path = tmp_path / "bad.dll"
    path.write_bytes(bytes(fixture.data))
    result = tool.analyse(path)
    assert result["status"] == "out_of_bounds"
    assert result["error"]["code"] == "out_of_bounds"


def test_unsupported_pe_machine_is_rejected(tmp_path: Path):
    fixture = build()
    struct.pack_into("<H", fixture.data, 0x84, 0x14C)  # i386
    path = tmp_path / "x86.dll"
    path.write_bytes(bytes(fixture.data))
    assert tool.analyse(path)["status"] == "unsupported"


def test_format_offset_outside_the_file_is_unsupported(tmp_path: Path):
    path = build(proc_offsets={3: 0x7FFF}).save(tmp_path / "proxy.dll")
    result = tool.analyse(path)
    assert result["ndr"]["status"] == "unsupported"
    assert "does not resolve inside the file" in result["ndr"]["detail"]
    assert result["slots"] == []


def test_implausible_dispatch_count_is_rejected(tmp_path: Path):
    path = build(bad_count=0).save(tmp_path / "zero.dll")
    result = tool.analyse(path)
    assert result["stub"]["status"] == "invalid"
    assert result["status"] == "unsupported"


def test_parameter_count_is_withheld_when_descriptors_are_not_file_backed(tmp_path: Path):
    """A header that fits, but whose descriptor region does not, keeps count null.

    ``data_raw_size`` is chosen so the 16-byte header at PROC_RVA+0xF0 ends
    exactly at the last file-backed byte of ``.data``: the header is readable,
    the six descriptor bytes after it are not. Nothing is invented in that gap.
    """
    fixture = build(data_raw_size=0x300, proc_offsets={3: 0xF0})
    # The override points slot 3 at this offset, so the header must really live
    # there; otherwise the test would only prove that zero bytes decode to zero.
    fixture.put(PROC_RVA + 0xF0, _proc_header(opnum=0, stack=0x23, params=1))
    path = fixture.save(tmp_path / "proxy.dll")
    result = tool.analyse(path)
    assert result["status"] == "ok"
    slot = next(item for item in result["slots"] if item["slot"] == 3)
    # The 16-byte header is file-backed, so the raw bytes and fixed fields exist.
    assert len(slot["raw_header_hex"]) == 32
    assert slot["opnum"] == 0
    assert slot["stack_size"] == 0x23
    # The count byte is present but its descriptors are not: no number is invented.
    assert slot["parameter_count_byte"] == 1
    assert slot["parameter_count"] is None
    assert slot["parameter_count_status"].startswith("not_decoded")
    # Unaffected slots still decode normally.
    other = next(item for item in result["slots"] if item["slot"] == 4)
    assert other["parameter_count"] == 1
    assert other["parameter_count_status"] == "decoded"


def test_null_dispatch_table_is_valid_for_an_interpreted_interface(tmp_path: Path):
    """MIDL leaves DispatchTable null for a purely interpreted object interface."""
    path = build(null_dispatch=True).save(tmp_path / "proxy.dll")
    result = tool.analyse(path)
    assert result["status"] == "ok"
    assert result["stub"]["status"] == "ok"
    # The count is still authoritative even though there is no local table.
    assert result["stub"]["dispatch_table_count"] == SLOTS
    assert result["stub"]["dispatch_table_va"] == "0x0"
    assert result["stub"]["dispatch_table_present"] is False
    assert result["stub"]["dispatch_table_rva"] is None
    assert result["server_info"]["dispatch_table_present"] is False
    assert result["ndr"]["status"] == "ok"
    assert len(result["slots"]) == SLOTS
    for slot in result["slots"][3:]:
        assert slot["parameter_count"] == 1


def test_null_dispatch_table_is_never_dereferenced(tmp_path: Path):
    """A null table must not be treated as an address at or near zero."""
    path = build(null_dispatch=True).save(tmp_path / "proxy.dll")
    result = tool.analyse(path)
    # No candidate reports a bounds failure caused by the zero address.
    for candidate in result["stub"]["candidates"]:
        assert candidate.get("status") != "out_of_bounds"
        assert candidate.get("detail") is None or "0x0" not in str(candidate.get("detail"))


# --- entry points -----------------------------------------------------------
def test_cli_writes_json_and_reports_unreadable_paths(tmp_path: Path, capsys):
    path = build().save(tmp_path / "proxy.dll")
    out = tmp_path / "report.json"
    assert tool.main([str(path), "--output", str(out)]) == 0
    report = json.loads(out.read_text(encoding="utf-8"))
    assert report["status"] == "ok"
    assert report["limitations"]

    missing = tmp_path / "absent.dll"
    assert tool.main([str(missing)]) == 1
    printed = json.loads(capsys.readouterr().out)
    assert printed["status"] == "unreadable"
