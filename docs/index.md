# Getting started

`Bonsai.Zarr` writes streaming numeric data to arrays in a [Zarr](https://zarr.dev) version 3 store. A store is a folder that holds a hierarchy of groups and arrays, and each array is split into chunk files that are written as data arrives.

## Writing an array

Connect a sequence of values to the <xref:Bonsai.Zarr.ZarrWriter> operator, then set `Path` to the store folder and `Name` to the name of the array inside the store, such as `device/temperature`. The operator creates the array when the first value arrives, together with a group at the store root and at every intermediate path, and shows the name as its label in the workflow.

The operator accepts two kinds of sequence:

- A sequence of values creates a one-dimensional array with one element per value.
- A sequence of arrays creates a two-dimensional array with one row per array. Every array must have the same length as the first array.

The values can be `bool`, a signed or unsigned integer of 8 to 64 bits, `float` or `double`, and enumerations are written as their underlying integer type. The operator returns the input sequence unchanged.

An array that already exists with the same name raises an error when the workflow starts writing. Set `Overwrite` to replace it instead.

## Writing a group of arrays

When each element of the sequence is a structure or a class, the operator writes each public field and property to its own array, in a group with the specified `Name`. Nested types are flattened, so a `Position` member with `X` and `Y` fields is written to the arrays `Position.X` and `Position.Y`. Set `ValueSelector` to write only some of the members.

Each member must be one of the value types listed above, or an array of them, so each element adds one row to every array in the group. Members of any other type, such as strings or dates, raise an error when the workflow is built.

As with a single array, a group that already exists with the same name raises an error. Set `Overwrite` to replace the group and everything it holds.

## Writing buffers of samples

Acquisition devices often deliver data in buffers, where each element of the sequence carries several consecutive samples. For example, each element might carry an array of timestamps and a matrix with one row per channel and one column per sample.

Set `BufferSelector` to the member that gives the number of samples in each element, such as the array of timestamps. The number of samples is the length of an array, or the number of columns of a matrix. Each member is then written with one row per sample:

- An array adds one row for each of its values.
- A matrix adds one row for each of its columns, with one value per channel.

Every member must have the same number of samples as the selected member, so the arrays in the group stay aligned. A member with a single value per element has only one sample, so it cannot be written together with buffers.

When each element is itself an array of consecutive samples, such as a buffer from a single channel, set `BufferSelector` to `it`. Each sample is then appended as one row of a single one-dimensional array. If `BufferSelector` is left empty, each array is instead written as one row of a two-dimensional array.

## Chunks and recovery

`ChunkLength` sets the number of rows stored in each chunk file. Each chunk file holds the same bytes as a raw binary file of its rows. The last chunk is padded with zeros when the workflow stops, whether the sequence completes, fails or is cancelled.

The number of rows in each array is written when the workflow stops. If the process ends unexpectedly, the chunk files still hold the data but every array reads as empty until it is repaired, as described in [Recovering an interrupted recording](articles/recovery.md).

## Reading the data in Python

The stores can be read with `zarr-python` 3.0 or later. Versions from 3.2 require Python 3.12 or later, and on Python 3.11 the latest supported version is 3.1.6.

The arrays can be opened with `zarr-python`:

```python
import zarr

array = zarr.open_array("session.zarr", path="device/temperature", mode="r")
data = array[:]
```

Every array records the names of its dimensions, and the arrays in a group share their first dimension, which lets `xarray` open the group as a dataset:

```python
import xarray as xr

dataset = xr.open_zarr("session.zarr", group="ephys", consolidated=False)
```
