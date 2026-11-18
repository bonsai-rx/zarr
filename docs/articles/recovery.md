# Recovering an interrupted recording

The operator writes the number of rows in each array when the workflow stops. If the process ends unexpectedly, the chunk files still hold the recorded data but every array reads as empty.

The repair script recovers these arrays. It reads the data type and row length that the operator wrote when it created each array, counts the complete rows in the chunk files and writes the recovered number of rows. Arrays in the same group are given the same length, so their rows stay aligned. Arrays that were closed normally are left unchanged.

The script never changes or removes recorded data. It only appends zeros where Zarr requires a complete chunk, and rows beyond the recovered length stay in the files.

Rows still held in the operator buffer when the process ended never reached the chunk files, so they cannot be recovered.

## Running the script

Run the script with [uv](https://docs.astral.sh/uv/), passing the store folder:

```
uv run https://bonsai-rx.org/zarr/scripts/repair.py session.zarr
```

The script uses only the Python standard library. It can also be [downloaded](../scripts/repair.py) and run with any Python 3.11 interpreter or later.

## Script source

[!code-python[](../scripts/repair.py)]
