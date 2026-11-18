# /// script
# requires-python = ">=3.11"
# dependencies = [
#     "numpy",
#     "xarray",
#     "zarr>=3.0",
# ]
# ///
"""Check the Zarr stores written by the Bonsai.Zarr test suite against zarr-python and xarray.

The stores are written by the InteropStore_WritesEveryDataTypeAndFrame test into the test
output folder of each target framework. Run the tests first, then run this script with uv:

    dotnet test
    uv run tests/interop/validate.py

Store paths can also be passed explicitly as arguments.
"""

import importlib.util
import shutil
import sys
import tempfile
from pathlib import Path

import numpy as np
import xarray as xr
import zarr

ROOT = Path(__file__).resolve().parents[2]

VALUE_COUNT = 10
ROW_COUNT = 7
ROW_LENGTH = 3
CHUNK_LENGTH = 4
FRAME_COUNT = 6
BUFFER_COUNT = 3
BUFFER_LENGTH = 5
CHANNEL_COUNT = 4
INTERRUPTED_COUNT = 10
INTERRUPTED_CONTIGUOUS_COUNT = 10000
INTERRUPTED_CONTIGUOUS_FRAME_COUNT = 30000
WRITER_BUFFER_SIZE = 65536


def flushed_rows(count, row_size):
    """Returns the rows of an open array that reached its file, in whole writer buffers."""
    buffer_size = max(row_size, WRITER_BUFFER_SIZE - WRITER_BUFFER_SIZE % row_size)
    return count * row_size // buffer_size * buffer_size // row_size


# Rows still in the writer buffer when the copy was taken are lost. Only complete chunks remain
# of a chunked array, and only the flushed part of the buffer of a single-file array.
RECOVERED_COUNT = INTERRUPTED_COUNT // CHUNK_LENGTH * CHUNK_LENGTH
RECOVERED_CONTIGUOUS_COUNT = flushed_rows(INTERRUPTED_CONTIGUOUS_COUNT, 8)
RECOVERED_TIME_COUNT = flushed_rows(INTERRUPTED_CONTIGUOUS_FRAME_COUNT, 8)
RECOVERED_SIGNAL_COUNT = flushed_rows(INTERRUPTED_CONTIGUOUS_FRAME_COUNT, 4)

VALUES = {
    "bool": lambda i: i % 3 == 0,
    "int8": lambda i: (i - 10) * 5,
    "int16": lambda i: (i - 10) * 1000,
    "int32": lambda i: (i - 10) * 100000,
    "int64": lambda i: (i - 10) * 10000000000,
    "uint8": lambda i: i * 10,
    "uint16": lambda i: i * 2000,
    "uint32": lambda i: i * 200000000,
    "uint64": lambda i: i * 800000000000000000,
    "float32": lambda i: (i - 10) * 0.25,
    "float64": lambda i: (i - 10) * 0.1,
}


def load_repair():
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location("repair", ROOT / "docs" / "scripts" / "repair.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def expected_values(dtype, count):
    return np.array([VALUES[dtype](i) for i in range(count)], dtype=dtype)


def check_array(group, path, dtype, expected, dimension_names, chunk_length=CHUNK_LENGTH):
    """Checks an array, which is stored in a single chunk if the chunk length is None."""
    array = group[path]
    assert isinstance(array, zarr.Array), f"{path} is not an array"
    assert array.dtype == np.dtype(dtype), f"{path}: dtype {array.dtype} != {dtype}"
    assert array.shape == expected.shape, f"{path}: shape {array.shape} != {expected.shape}"
    if chunk_length is None:
        assert array.chunks == array.shape, f"{path}: chunks {array.chunks} != shape {array.shape}"
    else:
        assert array.chunks[0] == chunk_length, f"{path}: chunks {array.chunks}"
    codecs = [codec["name"] for codec in array.metadata.to_dict()["codecs"]]
    assert codecs == ["bytes"], f"{path}: codecs {codecs}"
    assert array.metadata.dimension_names == dimension_names, f"{path}: dimension names {array.metadata.dimension_names}"
    np.testing.assert_array_equal(array[:], expected, err_msg=path)
    if len(expected) > 6:
        np.testing.assert_array_equal(array[3:6], expected[3:6], err_msg=f"{path}[3:6]")
    if len(expected) > 0:
        np.testing.assert_array_equal(array[-1], expected[-1], err_msg=f"{path}[-1]")


def frame_time(count):
    return np.arange(count) * 0.5


def frame_signal(count):
    return np.arange(2 * count).astype("int16").reshape(count, 2)


def check_frame(store_path, root, path, count, chunk_length=CHUNK_LENGTH):
    group_name = path.rsplit("/", 1)[-1]
    dimension = f"{group_name}_dim_0"
    check_array(root, f"{path}/Time", "float64", frame_time(count), (dimension,), chunk_length)
    check_array(root, f"{path}/Signal", "int16", frame_signal(count), (dimension, "Signal_dim_1"), chunk_length)
    dataset = xr.open_zarr(store_path, group=path, consolidated=False)
    assert dataset.sizes[dimension] == count, f"{path}: sizes {dict(dataset.sizes)}"
    assert dataset["Signal"].dims == (dimension, "Signal_dim_1"), f"{path}: dims {dataset['Signal'].dims}"
    np.testing.assert_array_equal(dataset["Time"].values, frame_time(count), err_msg=f"{path} xarray")


def check_buffers(store_path, root):
    samples = BUFFER_COUNT * BUFFER_LENGTH
    clock = np.arange(samples, dtype="uint64")
    check_array(root, "buffer/Clock", "uint64", clock, ("buffer_dim_0",))
    check_array(root, "buffer/Data", "float64", clock * 0.25, ("buffer_dim_0",))
    dataset = xr.open_zarr(store_path, group="buffer", consolidated=False)
    assert dataset.sizes["buffer_dim_0"] == samples, f"buffer: sizes {dict(dataset.sizes)}"
    checked = 2

    if "mat" in root:
        amplifier = np.array([[channel * 100 + t for channel in range(CHANNEL_COUNT)] for t in range(samples)], dtype="int16")
        check_array(root, "mat/Clock", "uint64", clock, ("mat_dim_0",))
        check_array(root, "mat/Amplifier", "int16", amplifier, ("mat_dim_0", "Amplifier_dim_1"))
        dataset = xr.open_zarr(store_path, group="mat", consolidated=False)
        assert dataset["Amplifier"].dims == ("mat_dim_0", "Amplifier_dim_1"), f"mat: dims {dataset['Amplifier'].dims}"
        checked += 2
    return checked


def check_interrupted(store_path, root):
    check_array(root, "closed/float64", "float64", np.arange(INTERRUPTED_COUNT, dtype="float64"), ("float64_dim_0",))
    check_frame(store_path, root, "closed/frame", INTERRUPTED_COUNT)
    contiguous = np.arange(INTERRUPTED_CONTIGUOUS_COUNT, dtype="float64")
    check_array(root, "closed/contiguous", "float64", contiguous, ("contiguous_dim_0",), None)
    for path in ("interrupted/float64", "interrupted/frame/Time", "interrupted/contiguous", "interrupted/contiguous_frame/Time"):
        assert root[path].shape == (0,), f"{path} is not empty"

    repair = load_repair()
    with tempfile.TemporaryDirectory() as temporary:
        copy_path = Path(temporary) / "interrupted"
        shutil.copytree(Path(store_path) / "interrupted", copy_path)
        repaired = repair.repair(copy_path)
        assert len(repaired) == 6, f"repaired {repaired}"
        repaired_root = zarr.open_group(copy_path, mode="r")
        check_array(repaired_root, "float64", "float64", np.arange(RECOVERED_COUNT, dtype="float64"), ("float64_dim_0",))
        check_frame(copy_path, repaired_root, "frame", RECOVERED_COUNT)
        recovered = contiguous[:RECOVERED_CONTIGUOUS_COUNT]
        check_array(repaired_root, "contiguous", "float64", recovered, ("contiguous_dim_0",), None)
        check_aligned_single_files(copy_path, repaired_root)
    return 11


def check_aligned_single_files(store_path, root):
    """Checks that aligning a single-file group keeps every recovered row in the files."""
    aligned = min(RECOVERED_TIME_COUNT, RECOVERED_SIGNAL_COUNT)
    dimension = ("contiguous_frame_dim_0",)
    check_array(root, "contiguous_frame/Time", "float64", frame_time(aligned), dimension, RECOVERED_TIME_COUNT)
    check_array(root, "contiguous_frame/Signal", "int16", frame_signal(aligned), (*dimension, "Signal_dim_1"), RECOVERED_SIGNAL_COUNT)
    stored_time = np.fromfile(Path(store_path) / "contiguous_frame" / "Time" / "c" / "0", dtype="float64")
    np.testing.assert_array_equal(stored_time, frame_time(RECOVERED_TIME_COUNT), err_msg="contiguous_frame/Time file")
    dataset = xr.open_zarr(store_path, group="contiguous_frame", consolidated=False)
    assert dataset.sizes[dimension[0]] == aligned, f"contiguous_frame: sizes {dict(dataset.sizes)}"


def validate(store_path):
    root = zarr.open_group(store_path, mode="r")
    checked = 0
    for dtype in VALUES:
        values = expected_values(dtype, VALUE_COUNT)
        check_array(root, f"values/{dtype}", dtype, values, (f"{dtype}_dim_0",))
        rows = expected_values(dtype, ROW_COUNT * ROW_LENGTH).reshape(ROW_COUNT, ROW_LENGTH)
        check_array(root, f"rows/{dtype}", dtype, rows, (f"{dtype}_dim_0", f"{dtype}_dim_1"))
        check_array(root, f"contiguous_values/{dtype}", dtype, values, (f"{dtype}_dim_0",), None)
        check_array(root, f"contiguous_rows/{dtype}", dtype, rows, (f"{dtype}_dim_0", f"{dtype}_dim_1"), None)
        checked += 4

    check_array(root, "disposed/float64", "float64", np.arange(6, dtype="float64"), ("float64_dim_0",))
    check_frame(store_path, root, "frame", FRAME_COUNT)
    check_frame(store_path, root, "contiguous_frame", FRAME_COUNT, None)
    checked += 5
    checked += check_buffers(store_path, root)
    checked += check_interrupted(store_path, root)
    return checked


def main(arguments):
    if arguments:
        store_paths = [Path(argument) for argument in arguments]
    else:
        store_paths = sorted((ROOT / "artifacts" / "bin" / "Bonsai.Zarr.Tests").glob("*/interop.zarr"))
    if not store_paths:
        print("No stores found. Run dotnet test first.", file=sys.stderr)
        return 1

    print(f"zarr {zarr.__version__}, xarray {xr.__version__}, numpy {np.__version__}")
    for store_path in store_paths:
        checked = validate(store_path)
        print(f"OK {store_path}: {checked} arrays")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
