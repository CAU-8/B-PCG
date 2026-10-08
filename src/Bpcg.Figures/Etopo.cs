using System;
using System.IO;
using Bpcg.Core;
using PureHDF;
using PureHDF.Selections;

namespace Bpcg.Figures;

/// <summary>
/// ETOPO 2022 (60″, netCDF4 = HDF5) 고도를 성기게 읽습니다 (render_results.py 의 h5py 읽기, z[::10, ::10]).
/// </summary>
/// <remarks>
/// PureHDF 로 성긴(stride) 선택이나 여러 청크에 걸친 선택을 읽으면 몇 분이 걸려서, 청크 크기의 네모로 나눠 읽은 뒤 골라 담습니다.
/// 파일의 청크는 1350 × 2700 이고 네모 크기가 달라도 값은 같습니다(속도만 다름). 값은 h5py 와 같습니다.
/// </remarks>
internal static class Etopo
{
    /// <summary>data/pilot/etopo 의 ETOPO 2022 표면 고도 파일.</summary>
    public static string DefaultPath => Path.Combine(Paths.Pilot, "etopo", "ETOPO_2022_v1_60s_N90W180_surface.nc");

    private const ulong TileRows = 1350;
    private const ulong TileCols = 2700;

    /// <summary>z[::stride, ::stride] (행 우선, float → double) 와 lat[::stride] [도].</summary>
    public static (double[] Z, double[] Lat, int Ny, int Nx) ReadStrided(string path, int stride = 10)
    {
        using var file = H5File.OpenRead(path);
        IH5Dataset ds = file.Dataset("z");
        ulong[] dims = ds.Space.Dimensions;
        if (dims.Length != 2)
        {
            throw new InvalidDataException($"{path}: z 가 2차원이 아닙니다");
        }
        ulong s = (ulong)stride, nyAll = dims[0], nxAll = dims[1];
        ulong ny = (nyAll + s - 1) / s, nx = (nxAll + s - 1) / s;
        double[] z = new double[ny * nx];
        for (ulong r0 = 0; r0 < nyAll; r0 += TileRows)
        {
            for (ulong c0 = 0; c0 < nxAll; c0 += TileCols)
            {
                ulong rows = Math.Min(TileRows, nyAll - r0), cols = Math.Min(TileCols, nxAll - c0);
                float[] buf = ds.Read<float[]>(new HyperslabSelection(2, [r0, c0], [rows, cols]), memoryDims: [rows * cols]);
                for (ulong r = r0 + ((s - (r0 % s)) % s); r < r0 + rows; r += s)
                {
                    for (ulong c = c0 + ((s - (c0 % s)) % s); c < c0 + cols; c += s)
                    {
                        z[((r / s) * nx) + (c / s)] = buf[((r - r0) * cols) + (c - c0)];
                    }
                }
            }
        }
        double[] latAll = file.Dataset("lat").Read<double[]>();
        double[] lat = new double[ny];
        for (ulong r = 0; r < ny; r++)
        {
            lat[r] = latAll[r * s];
        }
        return (z, lat, (int)ny, (int)nx);
    }
}
