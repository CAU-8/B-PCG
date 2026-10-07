using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;

namespace Bpcg.Compare;

/// <summary>
/// 예제 기반 패치 합성 (Galin 2019 5.1): 실제 DEM 조각을 겹쳐 이어 붙이는 이미지 퀼팅.
/// </summary>
/// <remarks>
/// Efros &amp; Freeman 2001 의 퀼팅을 높이 지도에 씁니다(Zhou 외 2007 의 패치 방식에서 스케치 안내를 뺀 꼴). 출력 격자를
/// 패치 크기 − 겹침 간격으로 채우며, 자리마다 예제에서 후보 패치를 해시로 뽑아 이미 채운 겹침 부분과의 제곱 오차가 가장 작은
/// 것을 고르고, 겹침 안에서는 오차가 가장 작은 경계선(동적 계획법)을 따라 잘라 붙입니다. relative 면 겹침의 평균 높이를 맞춰 붙여,
/// 절대 높이를 그대로 옮길 때 다양성이 줄어드는 문제(논문 5.1)를 피합니다. 논문 그림 26 처럼 물길이 전체로 이어진다는 보장은
/// 없습니다. 예제는 저장소의 시험 자료(tests/fixtures, 가봉 로페·라비 30 m 조각, 레이더 표면 높이라 숲 지붕이 섞임)나
/// .npz 파일(키 z, dx)입니다.
/// </remarks>
public static class QuiltingMethod
{
    private const long Stream = NoiseMethods.StreamBase + 501;

    /// <summary>선형 보간으로 크기를 바꿉니다 (scipy.ndimage.zoom order=1 꼴, 양 끝 칸을 맞춤). 반환: (값, ny, nx).</summary>
    public static (double[] Z, int Ny, int Nx) Zoom(double[] z, int ny, int nx, double factor)
    {
        int oy = Math.Max(2, (int)Math.Round(ny * factor));
        int ox = Math.Max(2, (int)Math.Round(nx * factor));
        double[] o = new double[oy * ox];
        for (int j = 0; j < oy; j++)
        {
            double fy = (double)j * (ny - 1) / (oy - 1);
            int j0 = Math.Min((int)fy, ny - 2);
            double ty = fy - j0;
            for (int i = 0; i < ox; i++)
            {
                double fx = (double)i * (nx - 1) / (ox - 1);
                int i0 = Math.Min((int)fx, nx - 2);
                double tx = fx - i0;
                o[(j * ox) + i] = (z[(j0 * nx) + i0] * (1 - tx) * (1 - ty)) + (z[(j0 * nx) + i0 + 1] * tx * (1 - ty))
                    + (z[((j0 + 1) * nx) + i0] * (1 - tx) * ty) + (z[((j0 + 1) * nx) + i0 + 1] * tx * ty);
            }
        }
        return (o, oy, ox);
    }

    /// <summary>세로 겹침 오차 (h, w) 의 위→아래 최소 오차 경계선. 반환: 새 패치를 쓸 칸 (선 오른쪽).</summary>
    private static bool[] SeamMask(double[] err, int h, int w)
    {
        double[] cost = (double[])err.Clone();
        for (int r = 1; r < h; r++)
        {
            for (int c = 0; c < w; c++)
            {
                double best = cost[((r - 1) * w) + c];
                if (c > 0)
                {
                    best = Math.Min(best, cost[((r - 1) * w) + c - 1]);
                }
                if (c < w - 1)
                {
                    best = Math.Min(best, cost[((r - 1) * w) + c + 1]);
                }
                cost[(r * w) + c] += best;
            }
        }
        bool[] mask = new bool[h * w];
        int col = 0;
        for (int c = 1; c < w; c++)
        {
            if (cost[((h - 1) * w) + c] < cost[((h - 1) * w) + col])
            {
                col = c;
            }
        }
        for (int r = h - 1; r >= 0; r--)
        {
            for (int c = col; c < w; c++)
            {
                mask[(r * w) + c] = true;
            }
            if (r > 0)
            {
                int lo = Math.Max(col - 1, 0);
                int hi = Math.Min(col + 2, w);
                int pick = lo;
                for (int c = lo + 1; c < hi; c++)
                {
                    if (cost[((r - 1) * w) + c] < cost[((r - 1) * w) + pick])
                    {
                        pick = c;
                    }
                }
                col = pick;
            }
        }
        return mask;
    }

    /// <summary>
    /// 예제 src (sy·sx,) 에서 n×n 지형을 퀼팅합니다. 반환: (고도, 칸마다 붙은 패치 번호 (없으면 −1), 패치별 겹침 평균 제곱 오차).
    /// onRow(줄 번호, 지금까지 채운 고도 (빈칸 NaN)) 은 패치 한 줄을 채울 때마다 부릅니다.
    /// </summary>
    public static (double[] Z, double[] PatchId, List<double> Errors) Quilt(
        double[] src, int sy, int sx, int n, int patch, int overlap, int candidates, long seed, bool relative, Action<int, double[]>? onRow)
    {
        patch = Math.Min(patch, Math.Min(sy, sx));
        overlap = Math.Min(overlap, patch / 2);
        int step = patch - overlap;
        double[] o = new double[n * n];
        double[] pid = Enumerable.Repeat(-1.0, n * n).ToArray();
        var errs = new List<double>();
        int k = 0;
        int row = 0;
        for (int by = 0; by < n; by += step, row++)
        {
            for (int bx = 0; bx < n; bx += step)
            {
                int h = Math.Min(patch, n - by);
                int w = Math.Min(patch, n - bx);
                bool top = by > 0;
                bool left = bx > 0;
                bool[] ov = new bool[h * w];
                int nOv = 0;
                for (int r = 0; r < h; r++)
                {
                    for (int c = 0; c < w; c++)
                    {
                        if ((top && r < overlap) || (left && c < overlap))
                        {
                            ov[(r * w) + c] = true;
                            nOv++;
                        }
                    }
                }
                double bestErr = double.PositiveInfinity;
                double[] best = [];
                int nCand = (top || left) ? candidates : 1;
                for (int cand = 0; cand < nCand; cand++)
                {
                    long key = ((long)k * 4096) + cand;
                    int y0 = (int)(Hashing.HashUnit(seed, key, Stream) * (sy - h + 1));
                    int x0 = (int)(Hashing.HashUnit(seed, key, Stream + 1) * (sx - w + 1));
                    double[] piece = new double[h * w];
                    for (int r = 0; r < h; r++)
                    {
                        Array.Copy(src, ((y0 + r) * sx) + x0, piece, r * w, w);
                    }
                    double off = 0.0;
                    if (nOv > 0 && relative)
                    {
                        double se = 0.0;
                        double sc = 0.0;
                        for (int r = 0; r < h; r++)
                        {
                            for (int c = 0; c < w; c++)
                            {
                                if (ov[(r * w) + c])
                                {
                                    se += o[((by + r) * n) + bx + c];
                                    sc += piece[(r * w) + c];
                                }
                            }
                        }
                        off = (se - sc) / nOv;
                    }
                    double err = 0.0;
                    for (int r = 0; r < h; r++)
                    {
                        for (int c = 0; c < w; c++)
                        {
                            piece[(r * w) + c] += off;
                            if (ov[(r * w) + c])
                            {
                                double d = piece[(r * w) + c] - o[((by + r) * n) + bx + c];
                                err += d * d;
                            }
                        }
                    }
                    if (err < bestErr || best.Length == 0)
                    {
                        bestErr = err;
                        best = piece;
                    }
                }
                bool[] take = Enumerable.Repeat(true, h * w).ToArray();
                double[] e2 = new double[h * w];
                for (int r = 0; r < h; r++)
                {
                    for (int c = 0; c < w; c++)
                    {
                        double d = best[(r * w) + c] - o[((by + r) * n) + bx + c];
                        e2[(r * w) + c] = d * d;
                    }
                }
                if (left)
                {
                    int ow = Math.Min(overlap, w);
                    double[] sub = new double[h * ow];
                    for (int r = 0; r < h; r++)
                    {
                        Array.Copy(e2, r * w, sub, r * ow, ow);
                    }
                    bool[] m = SeamMask(sub, h, ow);
                    for (int r = 0; r < h; r++)
                    {
                        for (int c = 0; c < ow; c++)
                        {
                            take[(r * w) + c] &= m[(r * ow) + c];
                        }
                    }
                }
                if (top)
                {
                    int oh = Math.Min(overlap, h);
                    double[] sub = new double[w * oh]; // 가로 겹침을 돌려 세로처럼 풉니다
                    for (int r = 0; r < oh; r++)
                    {
                        for (int c = 0; c < w; c++)
                        {
                            sub[(c * oh) + r] = e2[(r * w) + c];
                        }
                    }
                    bool[] m = SeamMask(sub, w, oh);
                    for (int r = 0; r < oh; r++)
                    {
                        for (int c = 0; c < w; c++)
                        {
                            take[(r * w) + c] &= m[(c * oh) + r];
                        }
                    }
                }
                for (int r = 0; r < h; r++)
                {
                    for (int c = 0; c < w; c++)
                    {
                        if (take[(r * w) + c])
                        {
                            o[((by + r) * n) + bx + c] = best[(r * w) + c];
                            pid[((by + r) * n) + bx + c] = k;
                        }
                    }
                }
                errs.Add(bestErr / Math.Max(nOv, 1));
                k++;
            }
            if (onRow is not null)
            {
                double[] view = new double[n * n];
                for (int c = 0; c < view.Length; c++)
                {
                    view[c] = pid[c] >= 0 ? o[c] : double.NaN;
                }
                onRow(row, view);
            }
        }
        return (o, pid, errs);
    }

    public static readonly CompareMethod Quilting = new(
        "quilting",
        "예제 패치 합성",
        "example",
        "Galin 2019 5.1 (그림 26·27); Zhou 외 2007, Efros & Freeman 2001",
        "실제 DEM 조각을 오차가 가장 작은 경계선으로 이어 붙임. 국소는 진짜, 물길은 보장 없음",
        [
            new Param("exemplar", "lope", "예제 지형: lope, rabi 또는 .npz 경로 (키 z, dx)", Kind: "str"),
            new Param("patch_px", 48L, "패치 한 변 (칸)", Kind: "int", Lo: 8, Hi: 512),
            new Param("overlap_px", 10L, "겹침 폭 (칸)", Kind: "int", Lo: 1, Hi: 128),
            new Param("candidates", 64L, "자리마다 비교할 후보 수", Kind: "int", Lo: 1, Hi: 4096),
            new Param("relative", true, "겹침 평균 높이를 맞춰 붙이기", Kind: "bool"),
            new Param("heights", "exemplar", "높이: exemplar 는 예제 높이 그대로(최저 0), grid 는 grid.relief_m 으로 맞춤", Kind: "str", Choices: ["exemplar", "grid"]),
        ],
        (grid, seed, p, ctx) =>
        {
            (double[] src, int sy, int sx, double dxSrc) = ctx.Exemplar(p.S("exemplar"));
            double factor = dxSrc / grid.Dx;
            if (Math.Abs(factor - 1.0) > 1e-9)
            {
                (src, sy, sx) = Zoom(src, sy, sx, factor);
            }
            var rows = new List<(int Row, double[] View)>();
            Action<int, double[]>? onRow = ctx.Recording ? (r, v) => rows.Add((r, v)) : null;
            (double[] z, double[] pid, List<double> errs) = Quilt(
                src, sy, sx, grid.N, p.I("patch_px"), p.I("overlap_px"), p.I("candidates"), seed, p.B("relative"), onRow);
            (double lo, _) = CompareGrid.MinMax(z);
            if (ctx.Recording)
            {
                using (ctx.Extra())
                {
                    int ey = Math.Min(sy, grid.N);
                    int ex = Math.Min(sx, grid.N);
                    double[] ex2 = Enumerable.Repeat(double.NaN, grid.Cells).ToArray();
                    double emin = double.PositiveInfinity;
                    for (int r = 0; r < ey; r++)
                    {
                        for (int c = 0; c < ex; c++)
                        {
                            emin = Math.Min(emin, src[(r * sx) + c]);
                        }
                    }
                    for (int r = 0; r < ey; r++)
                    {
                        for (int c = 0; c < ex; c++)
                        {
                            ex2[(r * grid.N) + c] = src[(r * sx) + c] - emin;
                        }
                    }
                    ctx.Snapshot("예제 지형 (일부)", ex2, unit: "m", note: $"예제 '{p.S("exemplar")}' 를 칸 간격 {grid.Dx:G} m 로 맞춘 것의 왼쪽 위입니다.");
                    int[] pick = [.. new[] { 0, rows.Count / 4, rows.Count / 2, 3 * rows.Count / 4 }.Distinct().Where(r => r < rows.Count - 1)];
                    foreach (int r in pick)
                    {
                        ctx.Snapshot($"{rows[r].Row + 1}번째 줄까지", Array.ConvertAll(rows[r].View, v => v - lo), unit: "m",
                            note: "패치를 줄마다 왼쪽에서 오른쪽으로 붙입니다. 빈칸은 회색입니다.");
                    }
                    ctx.Snapshot("완성", Array.ConvertAll(z, v => v - lo), unit: "m", note: "모든 자리를 채운 결과입니다.");
                    ctx.Snapshot("붙인 패치", pid, kind: "category", note: "칸마다 어느 패치에서 왔는지. 경계선은 겹침 오차가 가장 작은 선입니다.");
                    ctx.Trace("err", Enumerable.Range(1, errs.Count).Select(i => (double)i), errs, label: "고른 패치의 겹침 오차", unit: "m²", xlabel: "패치 번호");
                }
            }
            double[] outZ = p.S("heights") == "grid" ? CompareGrid.NormalizeRelief(z, grid.ReliefM) : Array.ConvertAll(z, v => v - lo);
            var output = new MethodOutput(outZ);
            output.Info["exemplar_shape"] = new List<object?> { (long)sy, (long)sx };
            output.Info["exemplar_dx_m"] = dxSrc;
            return output;
        });
}
