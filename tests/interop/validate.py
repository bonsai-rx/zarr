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

# Rows still in the writer buffer when the copy was taken are lost; only complete chunks remain.
RECOVERED_COUNT = INTERRUPTED_COUNT // CHUNK_LENGTH * CHUNK_LENGTH

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


def check_array(group, path, dtype, expected, dimension_names):
    array = group[path]
    assert isinstance(array, zarr.Array), f"{path} is not an array"
    assert array.dtype == np.dtype(dtype), f"{path}: dtype {array.dtype} != {dtype}"
    assert array.shape == expected.shape, f"{path}: shape {array.shape} != {expected.shape}"
    assert array.chunks[0] == CHUNK_LENGTH, f"{path}: chunks {array.chunks}"
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
    return np.arange(2 * count, dtype="int16").reshape(count, 2)


def check_frame(store_path, root, path, count):
    group_name = path.rsplit("/", 1)[-1]
    dimension = f"{group_name}_dim_0"
    check_array(root, f"{path}/Time", "float64", frame_time(count), (dimension,))
    check_array(root, f"{path}/Signal", "int16", frame_signal(count), (dimension, "Signal_dim_1"))
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
    assert root["interrupted/float64"].shape == (0,), "interrupted/float64 is not empty"
    assert root["interrupted/frame/Time"].shape == (0,), "interrupted/frame/Time is not empty"

    repair = load_repair()
    with tempfile.TemporaryDirectory() as temporary:
        copy_path = Path(temporary) / "interrupted"
        shutil.copytree(Path(store_path) / "interrupted", copy_path)
        repaired = repair.repair(copy_path)
        assert len(repaired) == 3, f"repaired {repaired}"
        repaired_root = zarr.open_group(copy_path, mode="r")
        check_array(repaired_root, "float64", "float64", np.arange(RECOVERED_COUNT, dtype="float64"), ("float64_dim_0",))
        check_frame(copy_path, repaired_root, "frame", RECOVERED_COUNT)
    return 7


def validate(store_path):
    root = zarr.open_group(store_path, mode="r")
    checked = 0
    for dtype in VALUES:
        values = expected_values(dtype, VALUE_COUNT)
        check_array(root, f"values/{dtype}", dtype, values, (f"{dtype}_dim_0",))
        rows = expected_values(dtype, ROW_COUNT * ROW_LENGTH).reshape(ROW_COUNT, ROW_LENGTH)
        check_array(root, f"rows/{dtype}", dtype, rows, (f"{dtype}_dim_0", f"{dtype}_dim_1"))
        checked += 2

    check_array(root, "disposed/float64", "float64", np.arange(6, dtype="float64"), ("float64_dim_0",))
    check_frame(store_path, root, "frame", FRAME_COUNT)
    checked += 3
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
