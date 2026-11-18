# /// script
# requires-python = ">=3.12"
# dependencies = [
#     "numpy",
#     "zarr==3.4.2",
# ]
# ///
"""Generate the reference stores compared byte for byte by the TestReferenceStores tests.

Each store is written by zarr-python with the same layout the Bonsai.Zarr writer produces:
uncompressed chunks, a fill value of zero, and dimension names derived from the node names.
Run the script with uv from the repository root, then commit the regenerated fixtures:

    uv run tests/interop/generate_fixtures.py
"""

import shutil
from pathlib import Path

import numpy as np
import zarr

FIXTURES = Path(__file__).resolve().parents[2] / "src" / "Bonsai.Zarr.Tests" / "Fixtures"


def u16_raw(path):
    array = zarr.create_array(path, shape=(10,), chunks=(4,), dtype="uint16", fill_value=0, compressors=None)
    array[:] = np.arange(10, dtype="uint16")


def f64_2d(path):
    array = zarr.create_array(path, shape=(5, 3), chunks=(2, 3), dtype="float64", fill_value=0.0, compressors=None)
    array[:] = np.arange(15, dtype="float64").reshape(5, 3) / 2


def group(path):
    root = zarr.open_group(path, mode="w")
    frame = root.create_group("frame")
    time = frame.create_array("Time", shape=(6,), chunks=(4,), dtype="float64", fill_value=0.0,
                              compressors=None, dimension_names=["frame_dim_0"])
    time[:] = np.arange(6) * 0.5
    signal = frame.create_array("Signal", shape=(6, 2), chunks=(4, 2), dtype="int16", fill_value=0,
                                compressors=None, dimension_names=["frame_dim_0", "Signal_dim_1"])
    signal[:] = np.arange(12, dtype="int16").reshape(6, 2)


def boolean(path):
    array = zarr.create_array(path, shape=(3,), chunks=(3,), dtype=bool, fill_value=False, compressors=None)
    array[:] = [True, False, True]


FIXTURE_WRITERS = {
    "a_u16_raw": u16_raw,
    "c_f64_2d": f64_2d,
    "d_group": group,
    "j_bool": boolean,
}


def main():
    print(f"zarr {zarr.__version__}, numpy {np.__version__}")
    for name, write in FIXTURE_WRITERS.items():
        path = FIXTURES / name
        shutil.rmtree(path, ignore_errors=True)
        write(str(path))
        print(f"wrote {path}")


if __name__ == "__main__":
    main()
