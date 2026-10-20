#!/usr/bin/env python3
"""Read-only static inspection of MIDL object-interface proxy/stub tables in a
PE32+ x64 DLL.

This tool answers one narrow, checkable question: for a given interface IID,
does this image contain a MIDL-generated interface stub table and proxy table,
and what do the per-slot NDR procedure headers say? It reports only what the
bytes in the file say, with an evidence RVA for every claim.

It deliberately does **not**:

* load, map, or execute the DLL (no ``LoadLibrary``, no ``ctypes``, no address
  dereference outside the file buffer);
* infer method names from opnums or slot numbers;
* claim that a slot can be called from another process;
* treat a C++ object's vtable as a COM interface dispatch table. A C++ virtual
  function and a COM method are different concepts, and a C++ non-virtual
  method can still be reached through a hand-written COM wrapper, so a name
  mangling never proves how (or whether) COM exposes something.

Everything is derived from file-backed bytes. A region that exists only in a
section's virtual size (zero fill at load time) is refused rather than read as
zeros, because zeros there are an artifact of the loader, not data.

Usage::

    python scripts/inspect_processing_proxy.py <dll> [--iid GUID] [--output out.json]

Exit code is 0 when the file was parsed, 1 when it could not be read at all.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import struct
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

#: The interface the bridge currently drives: ``ProcessingCom.DocId``.
DEFAULT_IID = "6E55B71A-944B-11D4-866C-00C04F68D1AC"

DOS_SIGNATURE = b"MZ"
PE_SIGNATURE = b"PE\x00\x00"
PE32P_MAGIC = 0x20B
MACHINE_AMD64 = 0x8664
SCN_MEM_EXECUTE = 0x20000000

# --- x64 MIDL layouts ------------------------------------------------------
# These match the structures MIDL emits for object interfaces under
# ``/Oicf`` on x64. They are *not* the older OLE proxy layouts: an object
# interface stub header carries the IID pointer, a MIDL_SERVER_INFO pointer,
# the dispatch count and the dispatch table, and the proxy table carries a
# MIDL_STUBLESS_PROXY_INFO pointer immediately before its IID pointer.
#
# CInterfaceStubHeader:
#   +0  const IID *piid
#   +8  MIDL_SERVER_INFO *pServerInfo
#   +16 unsigned int DispatchTableCount   (4 bytes + 4 bytes padding)
#   +24 RPC_DISPATCH_FUNCTION *DispatchTable
#   +32 LONG_PTR Reserved
STUB_PIID = 0
STUB_SERVER_INFO = 8
STUB_DISPATCH_COUNT = 16
STUB_DISPATCH_TABLE = 24

# CInterfaceProxyHeader:
#   +0  MIDL_STUBLESS_PROXY_INFO *pProxyInfo
#   +8  const IID *piid
#   +16 dispatch slots (IUnknown first)
PROXY_PROXY_INFO = 0
PROXY_PIID = 8
PROXY_VTBL = 16
IUNKNOWN_SLOTS = 3

# MIDL_SERVER_INFO (rpcndr.h):
#   +0  pStubDesc
#   +8  DispatchTable
#   +16 ProcString
#   +24 FmtStringOffset
#   +32 ThunkTable
#   +40 pTransferSyntax
#   +48 nCount          <- number of *transfer syntaxes*, NOT the method count
#   +56 pSyntaxInfo
#
# The only authoritative COM method count is the stub's DispatchTableCount.
# nCount describes how many transfer syntaxes (for example NDR32 + NDR64) the
# interface is marshalled for, and is typically 1 or 2.
SERVER_INFO_STUB_DESC = 0
SERVER_INFO_DISPATCH_TABLE = 8
SERVER_INFO_PROC_STRING = 16
SERVER_INFO_FORMAT_OFFSETS = 24
SERVER_INFO_THUNK_TABLE = 32
SERVER_INFO_TRANSFER_SYNTAX = 40
SERVER_INFO_SYNTAX_COUNT = 48
SERVER_INFO_SYNTAX_INFO = 56

# MIDL_STUBLESS_PROXY_INFO (rpcndr.h) has no dispatch/thunk table:
#   +0  pStubDesc
#   +8  ProcFormatString
#   +16 FormatStringOffset
#   +24 pTransferSyntax
#   +32 nCount          <- again a *transfer syntax* count
#   +40 pSyntaxInfo
PROXY_INFO_STUB_DESC = 0
PROXY_INFO_PROC_FORMAT = 8
PROXY_INFO_FORMAT_OFFSETS = 16
PROXY_INFO_TRANSFER_SYNTAX = 24
PROXY_INFO_SYNTAX_COUNT = 32
PROXY_INFO_SYNTAX_INFO = 40

#: A plausible number of transfer syntaxes. This is a sanity bound only; it is
#: never compared against the method count.
MAX_TRANSFER_SYNTAXES = 64

# Classic NDR procedure header, as emitted by MIDL for Oi2 object interfaces:
#   +0  u8  handle_type
#   +1  u8  oi_flags
#   +2  u16 rpc_flags
#   +6  u16 opnum
#   +8  u16 stack_size
#   +14 u8  oi2_flags
#   +15 u8  parameter count
#   +16 extension length, present only when oi2_flags & 0x40
#   descriptors begin at 16 + extension_length, 6 bytes each
PROC_HEADER_SIZE = 16
PROC_HANDLE_TYPE = 0
PROC_OI_FLAGS = 1
PROC_RPC_FLAGS = 2
PROC_OPNUM = 6
PROC_STACK_SIZE = 8
PROC_OI2_FLAGS = 14
PROC_PARAM_COUNT = 15
PROC_EXTENSION_LENGTH = 16
PROC_EXTENSION_FLAG = 0x40
PROC_DESCRIPTOR_SIZE = 6
#: Sanity ceiling for a decoded parameter count; a larger byte is not trusted.
PROC_MAX_PARAMETERS = 64

LIMITATIONS = [
    "Static ABI description only: slot numbers are opnums, not method names.",
    "A discovered slot does not prove that calling it from another process is safe.",
    "Slots 0-2 are reported as IUnknown without an NDR header; a dual interface would shift the method slots.",
    "Parameter counts are emitted only when the descriptor region validates inside the file; otherwise null.",
    "MIDL_SERVER_INFO.nCount and MIDL_STUBLESS_PROXY_INFO.nCount count transfer syntaxes, not methods.",
    "A C++ object's vtable is not a COM interface dispatch table; this tool never conflates them.",
    "A C++ non-virtual method can still be reached through a hand-written COM wrapper; mangling alone proves nothing about COM exposure.",
]


class PeError(Exception):
    """A structural problem with the image or with a referenced structure."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


@dataclass(frozen=True)
class Section:
    name: str
    virtual_address: int
    virtual_size: int
    raw_pointer: int
    raw_size: int
    characteristics: int

    @property
    def executable(self) -> bool:
        return bool(self.characteristics & SCN_MEM_EXECUTE)

    @property
    def file_backed_size(self) -> int:
        """Bytes of this section that exist in the file (never virtual fill)."""
        if not self.raw_size:
            return 0
        return min(self.raw_size, self.virtual_size) if self.virtual_size else self.raw_size


class PeImage:
    """A PE32+ image read strictly from a bytes buffer, with file-boundary checks."""

    def __init__(self, data: bytes) -> None:
        self.data = data
        self.sections: list[Section] = []
        self.image_base = 0
        self.size_of_image = 0
        self.size_of_headers = 0
        self.section_alignment = 0
        self.file_alignment = 0
        self._parse()

    def _parse(self) -> None:
        data = self.data
        if len(data) < 0x40:
            raise PeError("truncated", "file is shorter than the DOS header")
        if data[:2] != DOS_SIGNATURE:
            raise PeError("unsupported", "missing MZ signature")
        e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
        if e_lfanew < 0x40 or e_lfanew + 24 > len(data):
            raise PeError("truncated", "e_lfanew points outside the file")
        if data[e_lfanew : e_lfanew + 4] != PE_SIGNATURE:
            raise PeError("unsupported", "missing PE signature")
        coff = e_lfanew + 4
        machine, section_count = struct.unpack_from("<HH", data, coff)
        optional_size = struct.unpack_from("<H", data, coff + 16)[0]
        if machine != MACHINE_AMD64:
            raise PeError("unsupported", f"machine 0x{machine:04x} is not AMD64")
        if not 1 <= section_count <= 96:
            raise PeError("unsupported", f"section count {section_count} is implausible")
        optional = coff + 20
        if optional + optional_size > len(data):
            raise PeError("truncated", "optional header exceeds the file")
        if optional_size < 0x70:
            raise PeError("unsupported", "optional header is too small for PE32+")
        if struct.unpack_from("<H", data, optional)[0] != PE32P_MAGIC:
            raise PeError("unsupported", "not a PE32+ image")
        self.image_base = struct.unpack_from("<Q", data, optional + 24)[0]
        self.section_alignment = struct.unpack_from("<I", data, optional + 32)[0]
        self.file_alignment = struct.unpack_from("<I", data, optional + 36)[0]
        self.size_of_image = struct.unpack_from("<I", data, optional + 56)[0]
        self.size_of_headers = struct.unpack_from("<I", data, optional + 60)[0]
        table = optional + optional_size
        if table + section_count * 40 > len(data):
            raise PeError("truncated", "section table exceeds the file")
        for index in range(section_count):
            base = table + index * 40
            name = data[base : base + 8].split(b"\x00", 1)[0].decode("ascii", "replace")
            virtual_size, virtual_address, raw_size, raw_pointer = struct.unpack_from(
                "<IIII", data, base + 8
            )
            characteristics = struct.unpack_from("<I", data, base + 36)[0]
            if raw_size and raw_pointer + raw_size > len(data):
                raise PeError(
                    "out_of_bounds",
                    f"section {name} raw data 0x{raw_pointer:x}+0x{raw_size:x} exceeds the file",
                )
            self.sections.append(
                Section(name, virtual_address, virtual_size, raw_pointer, raw_size, characteristics)
            )

    def rva_to_offset(self, rva: int, size: int = 1) -> int:
        if size < 0 or rva < 0:
            raise PeError("out_of_bounds", "negative RVA or size")
        if rva < self.size_of_headers:
            limit = min(self.size_of_headers, len(self.data))
            if rva + size > limit:
                raise PeError("out_of_bounds", f"header RVA 0x{rva:x} (+{size}) is not file-backed")
            return rva
        for section in self.sections:
            span = max(section.virtual_size, 1)
            if section.virtual_address <= rva < section.virtual_address + span:
                delta = rva - section.virtual_address
                if delta + size > section.file_backed_size:
                    raise PeError(
                        "out_of_bounds",
                        f"RVA 0x{rva:x} (+{size}) lies in virtual-only space of {section.name}",
                    )
                return section.raw_pointer + delta
        raise PeError("out_of_bounds", f"RVA 0x{rva:x} is outside every section")

    def offset_to_rva(self, offset: int) -> int:
        if offset < 0:
            raise PeError("out_of_bounds", "negative file offset")
        if offset < min(self.size_of_headers, len(self.data)):
            return offset
        for section in self.sections:
            if section.raw_size and section.raw_pointer <= offset < section.raw_pointer + section.raw_size:
                delta = offset - section.raw_pointer
                if section.virtual_size and delta >= section.virtual_size:
                    raise PeError("out_of_bounds", f"file offset 0x{offset:x} is section padding")
                return section.virtual_address + delta
        raise PeError("out_of_bounds", f"file offset 0x{offset:x} is not inside a section")

    def va_to_rva(self, va: int) -> int:
        rva = va - self.image_base
        if rva < 0 or rva >= self.size_of_image:
            raise PeError("out_of_bounds", f"VA 0x{va:x} is outside the image")
        return rva

    def is_executable(self, rva: int) -> bool:
        for section in self.sections:
            span = max(section.virtual_size, 1)
            if section.virtual_address <= rva < section.virtual_address + span:
                return section.executable
        return False

    def read(self, rva: int, size: int) -> bytes:
        offset = self.rva_to_offset(rva, size)
        return self.data[offset : offset + size]

    def read_u16(self, rva: int) -> int:
        return struct.unpack("<H", self.read(rva, 2))[0]

    def read_u32(self, rva: int) -> int:
        return struct.unpack("<I", self.read(rva, 4))[0]

    def read_u64(self, rva: int) -> int:
        return struct.unpack("<Q", self.read(rva, 8))[0]

    def iter_qwords(self) -> Iterable[tuple[int, int]]:
        """Yield ``(rva, value)`` for 8-byte-aligned file data only."""
        for section in self.sections:
            usable = section.file_backed_size
            if usable < 8:
                continue
            # Skip a leading partial qword so every yielded RVA is aligned in VA
            # space, not merely at an 8-byte file offset.
            lead = (-(self.image_base + section.virtual_address)) % 8
            usable = usable - lead if usable > lead else 0
            usable -= usable % 8
            if usable < 8:
                continue
            start = section.raw_pointer + lead
            view = memoryview(self.data)[start : start + usable]
            base_rva = section.virtual_address + lead
            for index, (value,) in enumerate(struct.iter_unpack("<Q", view)):
                yield base_rva + index * 8, value


def _guid_bytes(iid: str) -> bytes:
    return uuid.UUID(iid).bytes_le


def _find_guid_occurrences(image: PeImage, needle: bytes) -> list[dict[str, Any]]:
    occurrences: list[dict[str, Any]] = []
    start = 0
    while True:
        index = image.data.find(needle, start)
        if index < 0:
            return occurrences
        start = index + 1
        entry: dict[str, Any] = {"file_offset": index}
        try:
            rva = image.offset_to_rva(index)
        except PeError as error:
            entry.update(rva=None, va=None, note=str(error))
            occurrences.append(entry)
            continue
        entry.update(rva=rva, va=image.image_base + rva)
        occurrences.append(entry)


def _find_references(image: PeImage, target_va: int) -> list[int]:
    return [rva for rva, value in image.iter_qwords() if value == target_va]


def _stub_candidate(image: PeImage, reference_rva: int, iid_va: int) -> dict[str, Any]:
    """Interpret a reference as the ``piid`` field of a ``CInterfaceStubVtbl``."""
    header = reference_rva - STUB_PIID
    info: dict[str, Any] = {"kind": "stub", "evidence_rva": header, "piid_rva": reference_rva}
    if header < 0:
        info.update(status="invalid", detail="header precedes the start of the image")
        return info
    try:
        if image.read_u64(header + STUB_PIID) != iid_va:
            raise PeError("invalid", "piid does not point at the requested IID")
        server_info_va = image.read_u64(header + STUB_SERVER_INFO)
        count = image.read_u32(header + STUB_DISPATCH_COUNT)
        dispatch_va = image.read_u64(header + STUB_DISPATCH_TABLE)
    except PeError as error:
        info.update(status=error.code, detail=str(error))
        return info
    info.update(
        server_info_va=hex(server_info_va),
        dispatch_table_count=count,
        dispatch_table_va=hex(dispatch_va),
    )
    if not 1 <= count <= 4096:
        info.update(status="invalid", detail=f"DispatchTableCount {count} is implausible")
        return info
    # A purely interpreted object interface has no local RPC dispatch table:
    # MIDL leaves DispatchTable null while still emitting a count and format
    # strings. A null table is therefore valid evidence, not a bounds error, and
    # the zero address must never be dereferenced.
    if dispatch_va == 0:
        info.update(
            status="ok",
            dispatch_table_rva=None,
            dispatch_table_present=False,
            dispatch_entries_executable=0,
            dispatch_entries_total=0,
        )
        return info
    try:
        table_rva = image.va_to_rva(dispatch_va)
        # Bounds are a hard requirement; executability is reported, not gated.
        # A dispatch entry may legitimately live in a section this parser cannot
        # classify, and rejecting the whole table over that would hide the one
        # value we actually need: the count.
        entries = [image.read_u64(table_rva + index * 8) for index in range(count)]
        executable = 0
        for entry_va in entries:
            try:
                if image.is_executable(image.va_to_rva(entry_va)):
                    executable += 1
            except PeError:
                continue
    except PeError as error:
        info.update(status=error.code, detail=str(error))
        return info
    info.update(
        status="ok",
        dispatch_table_rva=table_rva,
        dispatch_table_present=True,
        dispatch_entries_executable=executable,
        dispatch_entries_total=count,
    )
    return info


def _proxy_candidate(image: PeImage, reference_rva: int, iid_va: int) -> dict[str, Any]:
    """Interpret a reference as the ``piid`` field of a ``CInterfaceProxyVtbl``.

    The proxy header stores ``pProxyInfo`` immediately before ``piid``, and the
    dispatch slots follow the header inline. The slot *count* is never taken
    from here: only the stub's ``DispatchTableCount`` is authoritative.
    """
    header = reference_rva - PROXY_PIID
    info: dict[str, Any] = {"kind": "proxy", "evidence_rva": header, "piid_rva": reference_rva}
    if header < 0:
        info.update(status="invalid", detail="header precedes the start of the image")
        return info
    try:
        proxy_info_va = image.read_u64(header + PROXY_PROXY_INFO)
        if image.read_u64(header + PROXY_PIID) != iid_va:
            raise PeError("invalid", "piid does not point at the requested IID")
        slots = [
            image.read_u64(header + PROXY_VTBL + index * 8) for index in range(IUNKNOWN_SLOTS)
        ]
        # A real proxy table's first three entries are IUnknown thunks in
        # executable code. Requiring that is what keeps a stub header (whose
        # "slots" would be pServerInfo, the count, and zero) from being
        # accepted as a second proxy candidate.
        for index, slot_va in enumerate(slots):
            slot_rva = image.va_to_rva(slot_va)
            if not image.is_executable(slot_rva):
                raise PeError("invalid", f"IUnknown slot {index} is not executable code")
            image.rva_to_offset(slot_rva, 1)
        if proxy_info_va == 0:
            # A proxy table without a proxy-info block is still a valid table;
            # there is simply no transfer-syntax count to report.
            info.update(
                status="ok",
                proxy_info_va="0x0",
                proxy_info_present=False,
                proxy_info_rva=None,
                proxy_vtbl_rva=header + PROXY_VTBL,
                iunknown_slots=[hex(value) for value in slots],
            )
            return info
        proxy_info_rva = image.va_to_rva(proxy_info_va)
        # Only the transfer-syntax count at +32 is read here. The structure has
        # no method count, and +40 is pSyntaxInfo (a pointer).
        image.rva_to_offset(proxy_info_rva, PROXY_INFO_SYNTAX_INFO + 8)
        # ``unsigned long`` at +32, followed by 4 bytes of padding.
        syntax_count = image.read_u32(proxy_info_rva + PROXY_INFO_SYNTAX_COUNT)
    except PeError as error:
        info.update(status=error.code, detail=str(error))
        return info
    if not 1 <= syntax_count <= MAX_TRANSFER_SYNTAXES:
        info.update(
            status="invalid",
            detail=f"proxy info transfer-syntax count {syntax_count} is implausible",
        )
        return info
    info.update(
        status="ok",
        proxy_info_va=hex(proxy_info_va),
        proxy_info_present=True,
        proxy_info_rva=proxy_info_rva,
        transfer_syntax_count=syntax_count,
        proxy_vtbl_rva=header + PROXY_VTBL,
        iunknown_slots=[hex(value) for value in slots],
    )
    return info


def _server_info(image: PeImage, va: int, method_count: int) -> dict[str, Any]:
    """Read and validate ``MIDL_SERVER_INFO``.

    ``nCount`` here counts transfer syntaxes, not methods, so it is reported as
    ``transfer_syntax_count`` and never compared with the stub's
    ``DispatchTableCount``.
    """
    info: dict[str, Any] = {"kind": "server_info", "evidence_rva": None}
    try:
        rva = image.va_to_rva(va)
        info["evidence_rva"] = rva
        stub_desc = image.read_u64(rva + SERVER_INFO_STUB_DESC)
        dispatch_va = image.read_u64(rva + SERVER_INFO_DISPATCH_TABLE)
        proc_va = image.read_u64(rva + SERVER_INFO_PROC_STRING)
        offsets_va = image.read_u64(rva + SERVER_INFO_FORMAT_OFFSETS)
        # nCount is declared ``unsigned long`` (32 bits), followed by 4 bytes of
        # padding before pSyntaxInfo. Reading 4 bytes avoids depending on the
        # padding being zero.
        syntax_count = image.read_u32(rva + SERVER_INFO_SYNTAX_COUNT)
        # MIDL_SERVER_INFO.DispatchTable may be null for a purely interpreted
        # interface; the format strings are what we actually need. Only a
        # non-null table is bounds-checked, and the zero address is never
        # dereferenced.
        if dispatch_va:
            image.va_to_rva(dispatch_va)
        proc_rva = image.va_to_rva(proc_va)
        offsets_rva = image.va_to_rva(offsets_va)
        # The format-offset table must be file-backed for every slot it claims
        # to describe; a table that only exists virtually is not evidence.
        image.rva_to_offset(offsets_rva, 2 * method_count)
    except PeError as error:
        info.update(status=error.code, detail=str(error))
        return info
    info.update(
        stub_desc_va=hex(stub_desc),
        dispatch_table_va=hex(dispatch_va),
        dispatch_table_present=bool(dispatch_va),
        proc_string_rva=proc_rva,
        format_offsets_rva=offsets_rva,
        transfer_syntax_count=syntax_count,
    )
    if not 1 <= syntax_count <= MAX_TRANSFER_SYNTAXES:
        info.update(
            status="ambiguous",
            detail=f"MIDL_SERVER_INFO.nCount {syntax_count} is not a plausible transfer-syntax count",
        )
        return info
    info["status"] = "ok"
    return info


def _proxy_info(image: PeImage, va: int) -> dict[str, Any]:
    """Read and validate ``MIDL_STUBLESS_PROXY_INFO``.

    The structure has no dispatch table, and its ``nCount`` at +32 is a transfer
    syntax count. Offset +40 is ``pSyntaxInfo``, a pointer, so reading a count
    there would produce an arbitrary 64-bit value.
    """
    info: dict[str, Any] = {"kind": "proxy_info", "evidence_rva": None}
    try:
        rva = image.va_to_rva(va)
        info["evidence_rva"] = rva
        stub_desc = image.read_u64(rva + PROXY_INFO_STUB_DESC)
        proc_va = image.read_u64(rva + PROXY_INFO_PROC_FORMAT)
        offsets_va = image.read_u64(rva + PROXY_INFO_FORMAT_OFFSETS)
        # ``unsigned long`` at +32, then padding, then pSyntaxInfo at +40.
        syntax_count = image.read_u32(rva + PROXY_INFO_SYNTAX_COUNT)
        syntax_info_va = image.read_u64(rva + PROXY_INFO_SYNTAX_INFO)
        proc_rva = image.va_to_rva(proc_va)
        offsets_rva = image.va_to_rva(offsets_va)
    except PeError as error:
        info.update(status=error.code, detail=str(error))
        return info
    info.update(
        stub_desc_va=hex(stub_desc),
        proc_format_rva=proc_rva,
        format_offsets_rva=offsets_rva,
        transfer_syntax_count=syntax_count,
        syntax_info_va=hex(syntax_info_va),
    )
    if not 1 <= syntax_count <= MAX_TRANSFER_SYNTAXES:
        info.update(
            status="ambiguous",
            detail=f"MIDL_STUBLESS_PROXY_INFO.nCount {syntax_count} is not a plausible transfer-syntax count",
        )
        return info
    info["status"] = "ok"
    return info


def _decode_slot(image: PeImage, slot: int, header_rva: int, raw_offset: int) -> dict[str, Any]:
    """Decode one NDR procedure header, refusing to guess unsupported fields."""
    entry: dict[str, Any] = {
        "slot": slot,
        "kind": "method",
        "has_ndr_header": True,
        "midl_format_offset": raw_offset,
        "raw_header_hex": None,
        "handle_type": None,
        "oi_flags": None,
        "rpc_flags": None,
        "opnum": None,
        "stack_size": None,
        "oi2_flags": None,
        "parameter_count": None,
        "parameter_count_byte": None,
        "parameter_count_status": "not_decoded",
        "descriptors_start": None,
        "descriptor_bytes_hex": None,
    }
    try:
        header = image.read(header_rva, PROC_HEADER_SIZE)
    except PeError as error:
        entry.update(status="out_of_bounds", detail=str(error))
        return entry
    entry["raw_header_hex"] = header.hex()
    entry["handle_type"] = header[PROC_HANDLE_TYPE]
    entry["oi_flags"] = header[PROC_OI_FLAGS]
    entry["rpc_flags"] = struct.unpack_from("<H", header, PROC_RPC_FLAGS)[0]
    entry["opnum"] = struct.unpack_from("<H", header, PROC_OPNUM)[0]
    entry["stack_size"] = struct.unpack_from("<H", header, PROC_STACK_SIZE)[0]
    oi2_flags = header[PROC_OI2_FLAGS]
    entry["oi2_flags"] = oi2_flags
    count_byte = header[PROC_PARAM_COUNT]
    entry["parameter_count_byte"] = count_byte
    entry["status"] = "ok"

    # The parameter count byte is only meaningful when the descriptor region it
    # implies actually exists in the file. Otherwise the byte is reported raw
    # and the count stays null: no value is invented.
    if count_byte > PROC_MAX_PARAMETERS:
        entry["parameter_count_status"] = f"not_decoded: count byte {count_byte} exceeds {PROC_MAX_PARAMETERS}"
        return entry
    extension_length = 0
    if oi2_flags & PROC_EXTENSION_FLAG:
        try:
            extension_length = image.read(header_rva + PROC_EXTENSION_LENGTH, 1)[0]
        except PeError as error:
            entry["parameter_count_status"] = f"not_decoded: extension length unreadable ({error})"
            return entry
    descriptors_start = header_rva + PROC_HEADER_SIZE + extension_length
    entry["descriptors_start"] = descriptors_start
    descriptor_bytes = count_byte * PROC_DESCRIPTOR_SIZE
    try:
        if descriptor_bytes:
            raw = image.read(descriptors_start, descriptor_bytes)
            entry["descriptor_bytes_hex"] = raw.hex()
        else:
            image.rva_to_offset(descriptors_start, 0)
    except PeError as error:
        entry["parameter_count_status"] = f"not_decoded: descriptor region unreadable ({error})"
        return entry
    entry["parameter_count"] = count_byte
    entry["parameter_count_status"] = "decoded"
    return entry


def _decode_slots(image: PeImage, server: dict[str, Any], count: int) -> tuple[str, str | None, list[dict[str, Any]]]:
    """Read the per-slot NDR procedure headers from ``MIDL_SERVER_INFO``."""
    offsets_rva = server["format_offsets_rva"]
    proc_rva = server["proc_string_rva"]
    slots: list[dict[str, Any]] = []
    for slot in range(count):
        if slot < IUNKNOWN_SLOTS:
            # IUnknown is never described by a format string; reporting a
            # decoded header here would be an invention.
            slots.append(
                {
                    "slot": slot,
                    "kind": "iunknown",
                    "has_ndr_header": False,
                    "midl_format_offset": None,
                    "status": "not_described",
                }
            )
            continue
        try:
            raw_offset = image.read_u16(offsets_rva + slot * 2)
        except PeError as error:
            return (error.code, f"slot {slot}: format offset unreadable ({error})", [])
        # Offset 0 is a legitimate first entry: the table is relative to the
        # procedure format string, so it is decoded rather than skipped.
        try:
            header_rva = proc_rva + raw_offset
            image.rva_to_offset(header_rva, PROC_HEADER_SIZE)
        except PeError as error:
            return (
                "unsupported",
                f"slot {slot}: format offset {raw_offset} does not resolve inside the file ({error})",
                [],
            )
        slots.append(_decode_slot(image, slot, header_rva, raw_offset))
    return ("ok", None, slots)


def _pick(candidates: list[dict[str, Any]]) -> tuple[str, dict[str, Any] | None]:
    """Choose one usable candidate, or say precisely why none is usable."""
    valid = [item for item in candidates if item.get("status") == "ok"]
    if len(valid) == 1:
        return "ok", valid[0]
    if len(valid) > 1:
        return "ambiguous", None
    if not candidates:
        return "not_found", None
    codes = {str(item.get("status")) for item in candidates}
    if len(codes) == 1:
        return codes.pop(), None
    return "ambiguous", None


def analyse(path: str | Path, iid: str | None = None) -> dict[str, Any]:
    """Inspect ``path`` for the MIDL object-interface tables of ``iid``."""
    interface_id = iid or DEFAULT_IID
    result: dict[str, Any] = {
        "tool": "inspect_processing_proxy",
        "schema": 2,
        "iid_requested": interface_id,
        "limitations": LIMITATIONS,
    }
    try:
        data = Path(path).read_bytes()
    except OSError as error:
        result.update(status="unreadable", error={"code": "unreadable", "message": str(error)})
        return result
    result["file"] = {"path": str(path), "size": len(data), "sha256": hashlib.sha256(data).hexdigest()}
    try:
        image = PeImage(data)
    except PeError as error:
        result.update(status=error.code, error={"code": error.code, "message": str(error)})
        return result
    result["pe"] = {
        "machine": "AMD64",
        "magic": "PE32+",
        "image_base": hex(image.image_base),
        "size_of_image": image.size_of_image,
        "size_of_headers": image.size_of_headers,
        "sections": [
            {
                "name": section.name,
                "rva": hex(section.virtual_address),
                "virtual_size": section.virtual_size,
                "file_backed_size": section.file_backed_size,
                "raw_offset": hex(section.raw_pointer) if section.raw_size else None,
                "executable": section.executable,
            }
            for section in image.sections
        ],
    }
    try:
        needle = _guid_bytes(interface_id)
    except ValueError as error:
        result.update(status="invalid_iid", error={"code": "invalid_iid", "message": str(error)})
        return result
    result["iid"] = {"text": str(uuid.UUID(interface_id)).upper(), "bytes_le": needle.hex()}

    occurrences = _find_guid_occurrences(image, needle)
    result["iid"]["occurrences"] = occurrences

    # Only references that actually point at *this* IID's bytes are candidates.
    # A reference pointing at some other GUID is not evidence about this
    # interface, and mixing the two would manufacture false ambiguity.
    by_occurrence: list[tuple[int, list[int]]] = []
    all_references: set[int] = set()
    for occurrence in occurrences:
        if occurrence.get("va") is None:
            continue
        pointing = _find_references(image, occurrence["va"])
        all_references.update(pointing)
        by_occurrence.append((occurrence["va"], pointing))
    references = sorted(all_references)
    result["iid"]["references"] = [
        {"rva": rva, "va": hex(image.image_base + rva)} for rva in references
    ]

    stub_candidates: list[dict[str, Any]] = []
    proxy_candidates: list[dict[str, Any]] = []
    for iid_va, pointing in by_occurrence:
        for reference in pointing:
            stub_candidates.append(_stub_candidate(image, reference, iid_va))
            proxy_candidates.append(_proxy_candidate(image, reference, iid_va))
    stub_status, stub = _pick(stub_candidates)
    proxy_status, proxy = _pick(proxy_candidates)

    result["stub"] = {
        "status": stub_status,
        "evidence_rva": stub["evidence_rva"] if stub else None,
        "dispatch_table_count": stub["dispatch_table_count"] if stub else None,
        "dispatch_table_va": stub.get("dispatch_table_va") if stub else None,
        "dispatch_table_present": stub.get("dispatch_table_present") if stub else None,
        "dispatch_table_rva": stub.get("dispatch_table_rva") if stub else None,
        "dispatch_entries_total": stub.get("dispatch_entries_total") if stub else None,
        "dispatch_entries_executable": stub.get("dispatch_entries_executable") if stub else None,
        "candidates": stub_candidates,
    }
    result["proxy"] = {
        "status": proxy_status,
        "evidence_rva": proxy["evidence_rva"] if proxy else None,
        "proxy_vtbl_rva": proxy.get("proxy_vtbl_rva") if proxy else None,
        "transfer_syntax_count": proxy.get("transfer_syntax_count") if proxy else None,
        "iunknown_slots": proxy.get("iunknown_slots") if proxy else None,
        "candidates": proxy_candidates,
    }

    count = result["stub"]["dispatch_table_count"]
    server_info: dict[str, Any] = {"status": "not_found", "evidence_rva": None}
    proxy_info: dict[str, Any] = {"status": "not_found", "evidence_rva": None}
    slots: list[dict[str, Any]] = []
    ndr_status = "not_found"
    ndr_detail: str | None = None

    if stub and count:
        server_info = _server_info(image, int(stub["server_info_va"], 16), count)
        if server_info["status"] == "ok":
            ndr_status, ndr_detail, slots = _decode_slots(image, server_info, count)
    if proxy and count:
        proxy_info = _proxy_info(image, int(proxy["proxy_info_va"], 16))

    # The two tables are related only if they describe the same stub descriptor.
    if server_info["status"] == "ok" and proxy_info["status"] == "ok":
        same = server_info["stub_desc_va"] == proxy_info["stub_desc_va"]
        association = {
            "status": "ok" if same else "mismatch",
            "detail": "server and proxy MIDL structures share one MIDL_STUB_DESC",
        }
    else:
        association = {"status": "partial", "detail": "one of the MIDL structures is unavailable"}

    result["server_info"] = server_info
    result["proxy_info"] = proxy_info
    result["association"] = association
    result["ndr"] = {"status": ndr_status, "detail": ndr_detail, "slot_count": count}
    result["slots"] = slots

    components = [
        stub_status,
        proxy_status,
        server_info["status"],
        proxy_info["status"],
        association["status"],
        ndr_status,
    ]
    failure = {"unsupported", "invalid", "out_of_bounds", "truncated", "mismatch"}
    # A stub without a proxy table still proves the method count and the NDR
    # headers; that is the answer this tool exists to give. Proxy is reported
    # separately instead of downgrading the whole result.
    essential = [stub_status, server_info["status"], ndr_status]
    if all(item == "ok" for item in components) and len(slots) == count:
        overall = "ok"
    elif any(item in failure for item in essential) or association["status"] == "mismatch":
        overall = "unsupported"
    elif "ambiguous" in essential or proxy_status == "ambiguous":
        overall = "ambiguous"
    elif all(item == "ok" for item in essential) and len(slots) == count:
        overall = "partial"
    elif any(item in failure for item in components):
        overall = "unsupported"
    else:
        overall = "not_found"
    result["status"] = overall
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Static, read-only MIDL object-interface inventory for a PE32+ x64 DLL."
    )
    parser.add_argument("dll", help="path to the DLL to inspect (never loaded or executed)")
    parser.add_argument("--iid", default=DEFAULT_IID, help=f"interface GUID (default: {DEFAULT_IID})")
    parser.add_argument("--output", help="write JSON here instead of stdout")
    args = parser.parse_args(argv)
    result = analyse(args.dll, args.iid)
    text = json.dumps(result, indent=2, ensure_ascii=False)
    if args.output:
        Path(args.output).write_text(text + "\n", encoding="utf-8")
    else:
        print(text)
    return 0 if result.get("status") != "unreadable" else 1


if __name__ == "__main__":
    raise SystemExit(main())
