# /// script
# requires-python = ">=3.11"
# ///
"""Repair the arrays of a Bonsai.Zarr recording that was interrupted before it could close.

Bonsai.Zarr writes the shape of each array when the array is created, with no rows, and again
when the recording stops. If the process ends before the recording stops, the data is in the
chunk files but every array still reads as empty. This script recovers the number of rows of
each such array from the size of its chunk files, pads the last chunk with zeros, and writes the
recovered shape. Arrays in the same group that share their first dimension are cut to the
length of the shortest one, so they stay aligned.

Rows still held in the writer buffer when the process ended never reached the chunk files and
cannot be recovered. Arrays whose shape is not empty were closed normally and are left as they
are.

Run the script with uv, passing the store or any group or array inside it:

    uv run repair.py path/to/store.zarr
"""

import json
import math
import os
import sys
import uuid
from pathlib import Path

METADATA_KEY = "zarr.json"

ELEMENT_SIZES = {
    "bool": 1,
    "int8": 1,
    "uint8": 1,
    "int16": 2,
    "uint16": 2,
    "int32": 4,
    "uint32": 4,
    "float32": 4,
    "int64": 8,
    "uint64": 8,
    "float64": 8,
}


def read_metadata(node_path):
    return json.loads((node_path / METADATA_KEY).read_text(encoding="utf-8"))


def write_metadata(node_path, metadata):
    temporary_path = node_path / f"{METADATA_KEY}.{uuid.uuid4().hex}.tmp"
    temporary_path.write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    os.replace(temporary_path, node_path / METADATA_KEY)


def get_chunk_path(array_path, index, rank):
    return array_path.joinpath("c", str(index), *["0"] * (rank - 1))


def is_interrupted(metadata):
    return (
        metadata.get("node_type") == "array"
        and metadata["shape"][0] == 0
        and metadata["data_type"] in ELEMENT_SIZES
        and metadata["fill_value"] in (0, False)
    )


def recover_rows(array_path, metadata):
    """Returns the number of complete rows in the chunk files, padding the last chunk."""
    chunk_shape = metadata["chunk_grid"]["configuration"]["chunk_shape"]
    chunk_length = chunk_shape[0]
    row_size = math.prod(chunk_shape[1:]) * ELEMENT_SIZES[metadata["data_type"]]
    rank = len(chunk_shape)
    rows = 0
    index = 0
    while (chunk_path := get_chunk_path(array_path, index, rank)).exists():
        chunk_rows = min(chunk_path.stat().st_size // row_size, chunk_length)
        rows += chunk_rows
        if chunk_rows < chunk_length:
            with open(chunk_path, "r+b") as chunk:
                chunk.seek(chunk_rows * row_size)
                chunk.write(bytes((chunk_length - chunk_rows) * row_size))
                chunk.truncate()
            break
        index += 1
    return rows


def repair(path):
    """Repairs every interrupted array at or below the specified path.

    Returns a dictionary from each repaired array path to its recovered number of rows.
    """
    path = Path(path)
    interrupted = {}
    for metadata_path in sorted(path.rglob(METADATA_KEY)):
        array_path = metadata_path.parent
        metadata = read_metadata(array_path)
        if is_interrupted(metadata):
            interrupted[array_path] = (metadata, recover_rows(array_path, metadata))

    shared_lengths = {}
    for array_path, (metadata, rows) in interrupted.items():
        key = (array_path.parent, metadata["dimension_names"][0])
        shared_lengths[key] = min(rows, shared_lengths.get(key, rows))

    repaired = {}
    for array_path, (metadata, rows) in interrupted.items():
        rows = shared_lengths[(array_path.parent, metadata["dimension_names"][0])]
        if rows > 0:
            metadata["shape"][0] = rows
            write_metadata(array_path, metadata)
            repaired[array_path] = rows
    return repaired


def main(arguments):
    if len(arguments) != 1:
        print(__doc__, file=sys.stderr)
        return 1

    repaired = repair(arguments[0])
    for array_path, rows in repaired.items():
        print(f"{array_path}: {rows} rows")
    print(f"Repaired {len(repaired)} arrays.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
