"""Exercise the product C# parser without starting Avantage or its native worker.

The optional real-VGP case copies AVANTAGE_VGP_FIXTURE before reading it. No
user dataset is included in the repository, and no native EXE is overwritten.
"""
from __future__ import annotations

import base64
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess

import pytest


ROOT = Path(__file__).resolve().parents[1]
PARSER_SOURCE = ROOT / "native" / "ProjectGridValidation.cs"


@pytest.fixture(scope="module")
def parser(tmp_path_factory):
    if os.name != "nt":
        pytest.skip("The product parser is built with the Windows .NET compiler")
    candidates = (
        Path(r"C:\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"),
        Path(os.environ.get("WINDIR", r"C:\Windows"))
        / "Microsoft.NET" / "Framework64" / "v4.0.30319" / "csc.exe",
    )
    compiler = next((path for path in candidates if path.is_file()), None)
    if compiler is None:
        pytest.skip("A .NET Framework C# compiler is required")
    directory = tmp_path_factory.mktemp("grid-parser-probe")
    probe = directory / "GridParserProbe.cs"
    # Only storage I/O and the existing Fault type are stubbed. Every validation
    # decision is executed by the actual product source, not a Python copy.
    probe.write_text(r'''
using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
namespace AvantageMcp {
    public class Fault : Exception {
        public string Code;
        public Fault(string code, string message) : base(message) {Code=code;}
    }
    internal static class ProjectPersistence {
        internal static byte[] ReadStream(string path, params string[] names) {
            throw new NotSupportedException("This probe exercises the byte parser only.");
        }
    }
}
class GridParserProbe {
    static int Main() {
        var json=new JavaScriptSerializer();
        var input=json.Deserialize<Dictionary<string,string>>(Console.ReadLine());
        try {
            var result=AvantageMcp.ProjectGridValidation.Parse(
                Convert.FromBase64String(input["data_contents"]),
                Convert.FromBase64String(input["grid_contents"]),
                Convert.FromBase64String(input["data_xrefs"]),
                Convert.FromBase64String(input["grid_xrefs"]));
            Console.WriteLine(json.Serialize(new {snapshot=result}));
        } catch (AvantageMcp.Fault error) {
            Console.WriteLine(json.Serialize(new {code=error.Code, error=error.Message}));
        }
        return 0;
    }
}
''', encoding="utf-8")
    executable = directory / "GridParserProbe.exe"
    built = subprocess.run([
        str(compiler), "/nologo", "/target:exe", "/platform:x64",
        f"/out:{executable}", "/r:System.Web.Extensions.dll",
        str(PARSER_SOURCE), str(probe),
    ], capture_output=True, text=True, timeout=30)
    assert built.returncode == 0, built.stdout + built.stderr

    def run(streams):
        request = {name: base64.b64encode(value).decode("ascii")
                   for name, value in streams.items()}
        completed = subprocess.run(
            [str(executable)], input=json.dumps(request) + "\n", text=True,
            capture_output=True, timeout=15,
        )
        assert completed.returncode == 0, completed.stdout + completed.stderr
        return json.loads(completed.stdout)

    return run


def integers(*values):
    return struct.pack("<" + "i" * len(values), *values)


def references(*groups):
    result = integers(0)
    for pairs in groups:
        result += integers(len(pairs))
        result += b"".join(integers(*pair) for pair in pairs)
        result += integers(0)
    return result


def valid_streams():
    return {
        "data_contents": integers(1, 3) + b"opaque data metadata",
        "grid_contents": integers(1, 2) + b"opaque chart metadata",
        "data_xrefs": references([(2, 1), (1, 0)], [], []),
        "grid_xrefs": references([(0, 1), (1, 2)]),
    }


def invalid(parser, streams):
    result = parser(streams)
    assert result["code"] == "INVALID_PROJECT_GRID"
    assert "snapshot" not in result


def test_snapshot_describes_grid_without_paths_or_spectrum_values(parser):
    streams = valid_streams()
    snapshot = parser(streams)["snapshot"]
    assert snapshot == {
        "spectrum_count": 3, "grid_cell_count": 2,
        "grid_data_pairs": [[0, 1], [1, 2]],
        "mapped_grid_cell_count": 2, "visible_spectrum_count": 2,
        "grid_contents_sha256": hashlib.sha256(streams["grid_contents"]).hexdigest().upper(),
    }


def test_same_spectrum_in_several_cells_is_valid(parser):
    streams = valid_streams()
    streams["data_xrefs"] = references([(1, 0), (1, 1)], [], [])
    streams["grid_xrefs"] = references([(1, 1), (0, 1)])
    snapshot = parser(streams)["snapshot"]
    assert snapshot["grid_data_pairs"] == [[0, 1], [1, 1]]
    assert snapshot["visible_spectrum_count"] == 1
    assert snapshot["mapped_grid_cell_count"] == 2


def test_reference_order_is_normalized_but_duplicate_multiplicity_is_checked(parser):
    streams = valid_streams()
    expected = parser(streams)["snapshot"]
    streams["grid_xrefs"] = references([(1, 2), (0, 1)])
    assert parser(streams)["snapshot"] == expected
    streams["grid_xrefs"] = references([(1, 2), (0, 1), (0, 1)])
    invalid(parser, streams)


def test_empty_collections_are_valid_and_opaque_layout_changes_are_detected(parser):
    streams = {
        "data_contents": integers(1, 0), "grid_contents": integers(1, 0),
        "data_xrefs": references([], [], []), "grid_xrefs": references([]),
    }
    first = parser(streams)["snapshot"]
    assert first["grid_data_pairs"] == []
    streams["grid_contents"] += b"opaque layout change"
    assert parser(streams)["snapshot"] != first


def test_known_legacy_contents_schema_zero_is_accepted(parser):
    streams = valid_streams()
    for name in ("data_contents", "grid_contents"):
        streams[name] = integers(0) + streams[name][4:]
    assert parser(streams)["snapshot"]["grid_cell_count"] == 2


@pytest.mark.parametrize("name", ["data_contents", "grid_contents", "data_xrefs", "grid_xrefs"])
@pytest.mark.parametrize("length", [0, 1, 3, 4, 7])
def test_truncated_headers_fail(parser, name, length):
    streams = valid_streams()
    streams[name] = streams[name][:length]
    invalid(parser, streams)


@pytest.mark.parametrize("name", ["data_contents", "grid_contents", "data_xrefs", "grid_xrefs"])
def test_unsupported_versions_fail(parser, name):
    streams = valid_streams()
    streams[name] = integers(2) + streams[name][4:]
    invalid(parser, streams)


@pytest.mark.parametrize("count", [-1, 2147483647])
@pytest.mark.parametrize("name", ["data_contents", "grid_contents", "data_xrefs", "grid_xrefs"])
def test_negative_or_excessive_counts_fail_before_allocation(parser, name, count):
    streams = valid_streams()
    streams[name] = streams[name][:4] + integers(count) + streams[name][8:]
    invalid(parser, streams)


@pytest.mark.parametrize("name", ["data_xrefs", "grid_xrefs"])
def test_trailing_data_missing_groups_and_invalid_terminators_fail(parser, name):
    for value in (valid_streams()[name] + integers(0), valid_streams()[name][:-4],
                  valid_streams()[name][:-4] + integers(1)):
        streams = valid_streams()
        streams[name] = value
        invalid(parser, streams)


@pytest.mark.parametrize("data_edges,cell_edges", [
    ([(3, 0)], [(0, 3)]), ([(1, 2)], [(2, 1)]),
    ([(-1, 0)], [(0, -1)]), ([(1, -1)], [(-1, 1)]),
    ([(1, 0)], [(1, 1)]), ([(1, 0)], []),
])
def test_out_of_range_or_nonreciprocal_edges_fail(parser, data_edges, cell_edges):
    streams = valid_streams()
    streams["data_xrefs"] = references(data_edges, [], [])
    streams["grid_xrefs"] = references(cell_edges)
    invalid(parser, streams)


def test_all_three_data_groups_are_parsed_without_guessing_their_target_collections(parser):
    streams = valid_streams()
    # The other two groups can change during model edits. Their serialization
    # must still be complete; their semantic target counts are outside this API.
    streams["data_xrefs"] = references([(2, 1), (1, 0)], [(1, 4)], [(1, 0)])
    assert "snapshot" in parser(streams)
    damaged = bytearray(streams["data_xrefs"])
    damaged[-4:] = integers(3)
    streams["data_xrefs"] = bytes(damaged)
    invalid(parser, streams)


@pytest.mark.skipif(
    os.environ.get("AVANTAGE_INTEGRATION") != "1" or not os.environ.get("AVANTAGE_VGP_FIXTURE"),
    reason="Requires an explicitly supplied VGP fixture",
)
def test_private_real_project_has_reciprocal_grid_references(parser, tmp_path):
    pythoncom = pytest.importorskip("pythoncom")
    source = Path(os.environ["AVANTAGE_VGP_FIXTURE"])
    if not source.is_file() or not source.with_suffix(".DATA").is_dir():
        pytest.skip("The VGP fixture and matching .DATA directory are required")
    source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
    private = tmp_path / source.name
    shutil.copy2(source, private)
    shutil.copytree(source.with_suffix(".DATA"), private.with_suffix(".DATA"))
    storage = pythoncom.StgOpenStorage(str(private), None, 0x20, None, 0)
    streams = {}
    for key, section, stream_name in (
        ("data_contents", "Data Spaces", "Contents"),
        ("grid_contents", "Grid Cells", "Contents"),
        ("data_xrefs", "Data Spaces", "XRefs"),
        ("grid_xrefs", "Grid Cells", "XRefs"),
    ):
        child = storage.OpenStorage(section, None, 0x10, None, 0)
        stream = child.OpenStream(stream_name, None, 0x10, 0)
        size = stream.Stat()[2]
        streams[key] = stream.Read(size) if size else b""
    first = parser(streams)["snapshot"]
    assert first["spectrum_count"] == struct.unpack_from("<i", streams["data_contents"], 4)[0]
    assert first["grid_cell_count"] == struct.unpack_from("<i", streams["grid_contents"], 4)[0]
    assert 0 <= first["visible_spectrum_count"] <= first["spectrum_count"]
    assert 0 <= first["mapped_grid_cell_count"] <= first["grid_cell_count"]
    assert parser(streams)["snapshot"] == first
    assert hashlib.sha256(source.read_bytes()).hexdigest() == source_hash
