using System;
using System.Collections.Generic;
using Bpcg.Core;

namespace Bpcg.Hydro;

/// <summary>호수 물수지의 결과 (LakeBalance.Solve).</summary>
/// <param name="Level">(N,) 넘치지 못하는 호수의 수면 [m]. 그런 호수 칸이 아니면 NaN.</param>
/// <param name="Closed">(N,) 넘치지 못하는 호수 칸 (닫힌 호수).</param>
/// <param name="Sink">(N,) 물이 더 흘러가지 않고 끝나는 칸: 닫힌 호수 칸과, 호수도 못 되고 마른 분지의 가장 낮은 칸.</param>
/// <param name="NOpen">넘쳐서 흘러 나가는 호수 수.</param>
/// <param name="NClosed">넘치지 못하는 호수 수.</param>
/// <param name="NDry">물이 한 칸도 채우지 못한 마른 분지 수.</param>
/// <param name="Passes">분지를 다시 찾아 계산한 횟수.</param>
public sealed record LakeBalanceResult(double[] Level, bool[] Closed, bool[] Sink, int NOpen, int NClosed, int NDry, int Passes);

/// <summary>
/// 호수 물수지 (docs/pipeline.md 8.1). 우묵한 자리에 물이 넘칠 때까지 차는지, 차다가 마는지를 정합니다.
/// </summary>
/// <remarks>
/// 왜 필요한가: 우묵한 자리를 넘칠 높이까지 채우기만 하면 사막 한가운데에도 물이 가득 찬 호수가 생깁니다. 지구의 마른 땅에서는
/// 들어오는 강물보다 수면에서 증발하는 물이 많아, 호수가 넘치지 못하고 강이 거기서 끝납니다 (사해, 그레이트솔트호).
/// 규칙: 호수 수면은 한 해에 잠재 증발산 PET 만큼 증발하고, 같은 땅이 물에 잠기지 않았다면 실제 증발산 AET = P − R
/// (강수 − 유출)만큼만 증발합니다. 그래서 땅 한 칸이 물에 잠길 때마다 (PET − AET) × 칸 넓이만큼 물이 더 듭니다.
/// 분지로 들어오는 물(둘레에서 흘러드는 유출 + 분지 안의 유출)이 분지를 다 잠그는 데 드는 물보다 많으면 호수는 넘칩니다.
/// 모자라면 낮은 칸부터 잠그다가 물이 떨어지는 높이에서 멈춥니다. 닫힌 분지 호수의 넓이를 정하는 물수지 식과 같습니다
/// (Huybers, Rupper &amp; Roe 2015 의 식 1·4; Garcia-Castellanos 2006 의 식 2): 호수 넓이 × (증발 − 강수) = 나머지 유역 넓이 × 유출.
/// Huybers 외는 이 식으로 그레이트솔트호에 들어오는 물을 한 해 2.5 km³ 로 계산했고, 잰 값은 2.3~2.8 km³ 입니다.
/// 예: 한 해 유출 0.05 m 인 유역 10,000 km² 에서 5억 m³ 가 들어오고, 호수 수면이 땅보다 한 해 1.0 m 더 증발하면
/// 호수는 500 km² 에서 멈춥니다.
/// 한계: 한 분지 안에 턱으로 나뉜 바닥이 여럿이면 가장 낮은 칸부터 한 수면으로 잠급니다 (바닥마다 따로 풀지 않음).
/// 넘치는 호수의 증발은 하류 유량에서 빼지 않습니다.
/// </remarks>
public static class LakeBalance
{
    /// <summary>물이 고였다고 보는 깊이의 아래 한계 [m] (Water.LakeMinDepthM 과 같은 값).</summary>
    public const double PondedM = 1e-3;

    /// <summary>분지를 다시 찾는 횟수의 상한. 닫힌 호수 위쪽의 마른 바닥에 남은 작은 분지를 마저 풉니다.</summary>
    public const int MaxPasses = 6;

    /// <summary>
    /// 물수지를 풉니다. z: (N,) 호수 바닥까지 낮춘 지표 [m]. isOutlet: (N,) 바다나 유역 출구.
    /// runoff: (N,) 실제 유출 [m/yr] (바닥값을 주지 않은 값). precip, pet: (N,) 강수와 잠재 증발산 [m/yr].
    /// inflow: (N,) 바깥에서 그 칸으로 들어오는 물 [m³/yr] (없으면 null). routeEps: 물길을 정할 때 쓰는 채움 기울기 [m].
    /// 물길은 채운 지표에서 가장 가파른 이웃으로 정합니다.
    /// </summary>
    public static LakeBalanceResult Solve(
        CellGraph graph, double[] z, bool[] isOutlet, double[] runoff, double[] precip, double[] pet, double[]? inflow,
        double routeEps) =>
        Solve(graph, z, isOutlet, runoff, precip, pet, inflow, outlet =>
        {
            double[] zt = Depressions.FillEpsilon(z, graph.Nbr, outlet, routeEps);
            (long[] rcv, _, _) = Routing.D8Receivers(zt, graph.Nbr, graph.Dist, outlet);
            return (rcv, Routing.TopoOrder(rcv));
        });

    /// <summary>
    /// 위와 같되 물길을 route 로 정합니다. route 는 '물이 끝나는 칸'(출구와 지금까지 찾은 닫힌 호수 칸)을 받아
    /// (receiver, 하류가 먼저 오는 순서)를 돌려줍니다. 생성 단계가 뒤에서 쓸 물길과 같은 규칙을 넘겨야, 여기서 센 '들어오는 물'이
    /// 결과의 강과 맞습니다.
    /// </summary>
    public static LakeBalanceResult Solve(
        CellGraph graph, double[] z, bool[] isOutlet, double[] runoff, double[] precip, double[] pet, double[]? inflow,
        Func<bool[], (long[] Receiver, long[] Order)> route)
    {
        int n = graph.NCells;
        if (z.Length != n || isOutlet.Length != n || runoff.Length != n || precip.Length != n || pet.Length != n
            || (inflow is not null && inflow.Length != n))
        {
            throw new ArgumentException(
                $"z, isOutlet, runoff, precip, pet, inflow 는 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이: {z.Length}, "
                + $"{isOutlet.Length}, {runoff.Length}, {precip.Length}, {pet.Length}, {inflow?.Length.ToString() ?? "없음"}");
        }
        double[] area = graph.Area;
        int[] nbr = graph.Nbr;
        // 칸이 물에 잠기면 한 해에 더 드는 물 [m³/yr] = (PET − AET) × 넓이, AET = P − R. 0 보다 작아지지 않게 자릅니다.
        double[] cost = new double[n];
        double[] source = new double[n];
        for (int c = 0; c < n; c++)
        {
            if (!(double.IsFinite(runoff[c]) && double.IsFinite(precip[c]) && double.IsFinite(pet[c])))
            {
                throw new ArgumentException($"칸 {c} 의 runoff, precip, pet 에 NaN 이나 inf 가 있습니다: {runoff[c]}, {precip[c]}, {pet[c]}");
            }
            double aet = Math.Max(precip[c] - Math.Max(runoff[c], 0.0), 0.0);
            cost[c] = Math.Max(pet[c] - aet, 0.0) * area[c];
            source[c] = (Math.Max(runoff[c], 0.0) * area[c]) + (inflow is null ? 0.0 : Math.Max(inflow[c], 0.0));
        }

        double[] level = new double[n];
        Array.Fill(level, double.NaN);
        bool[] closed = new bool[n];
        bool[] sink = new bool[n];
        bool[] outlet = (bool[])isOutlet.Clone();
        int nClosed = 0;
        int nDry = 0;
        int nOpen = 0;
        int passes = 0;
        int[] label = new int[n];
        var stack = new Stack<int>();
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            passes = pass + 1;
            double[] zHat = Depressions.FillDepressions(z, nbr, outlet);
            // --- 분지: 서로 이웃한 고인 칸의 묶음
            Array.Fill(label, -1);
            var basins = new List<List<int>>();
            for (int c0 = 0; c0 < n; c0++)
            {
                if (outlet[c0] || label[c0] >= 0 || !(zHat[c0] - z[c0] > PondedM))
                {
                    continue;
                }
                var cells = new List<int>();
                label[c0] = basins.Count;
                stack.Push(c0);
                while (stack.Count > 0)
                {
                    int c = stack.Pop();
                    cells.Add(c);
                    for (int s = 0; s < CellGraph.NSlots; s++)
                    {
                        int v = nbr[(c * CellGraph.NSlots) + s];
                        if (v >= 0 && label[v] < 0 && !outlet[v] && zHat[v] - z[v] > PondedM)
                        {
                            label[v] = basins.Count;
                            stack.Push(v);
                        }
                    }
                }
                basins.Add(cells);
            }
            if (basins.Count == 0)
            {
                nOpen = 0;
                break;
            }
            // --- 물길: 분지 안의 물은 넘치는 곳으로 모입니다.
            (long[] rcv, long[] order) = route(outlet);
            if (rcv.Length != n || order.Length != n)
            {
                throw new ArgumentException($"route 가 돌려준 receiver, order 는 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이: {rcv.Length}, {order.Length}");
            }
            // 분지마다 다 잠그는 데 드는 물.
            double[] fullCost = new double[basins.Count];
            for (int j = 0; j < basins.Count; j++)
            {
                foreach (int c in basins[j])
                {
                    fullCost[j] += cost[c];
                }
            }
            // 분지에서 물이 빠져나가는 칸. 분지 가장자리의 얕은 칸(깊이 1 mm 미만이라 분지에 넣지 않은 칸)을 거쳐 물길이
            // 분지를 나갔다가 곧 다시 들어오기도 합니다. 그런 칸은 출구로 세지 않습니다: 하류로 가며 처음 만나는 분지가
            // 자기 분지가 아닌 칸만 출구입니다. order 는 하류가 먼저라, 앞에서부터 돌면 수신 셀의 값이 먼저 정해집니다.
            int[] nextBasin = new int[n];
            for (int k = 0; k < n; k++)
            {
                int c = (int)order[k];
                int r = (int)rcv[c];
                nextBasin[c] = r == c ? -1 : (label[r] >= 0 ? label[r] : nextBasin[r]);
            }
            bool[] isExit = new bool[n];
            for (int c = 0; c < n; c++)
            {
                isExit[c] = label[c] >= 0 && rcv[c] != c && nextBasin[c] != label[c];
            }
            // --- 위쪽 분지가 닫히면 아래쪽 분지로 가는 물이 줄어듭니다. 닫힌 분지가 더 늘지 않을 때까지 다시 모읍니다.
            bool[] basinClosed = new bool[basins.Count];
            double[] basinIn = new double[basins.Count];
            double[] pass2 = new double[n];
            while (true)
            {
                Array.Clear(basinIn);
                Array.Copy(source, pass2, n);
                // 뒤에서부터 돌면 상류가 먼저입니다.
                for (int k = n - 1; k >= 0; k--)
                {
                    int c = (int)order[k];
                    int r = (int)rcv[c];
                    if (r == c)
                    {
                        continue;
                    }
                    if (isExit[c])
                    {
                        // 분지의 출구 칸: 분지에 모인 물을 세고, 닫힌 분지면 아래로 넘기지 않습니다.
                        int jc = label[c];
                        basinIn[jc] += pass2[c];
                        if (basinClosed[jc])
                        {
                            continue;
                        }
                    }
                    pass2[r] += pass2[c];
                }
                bool changed = false;
                for (int j = 0; j < basins.Count; j++)
                {
                    if (!basinClosed[j] && basinIn[j] < fullCost[j])
                    {
                        basinClosed[j] = true;
                        changed = true;
                    }
                }
                if (!changed)
                {
                    break;
                }
            }
            // --- 닫힌 분지: 낮은 칸부터 잠그다가 물이 떨어지는 곳에서 멈춥니다.
            int newSinks = 0;
            nOpen = 0;
            for (int j = 0; j < basins.Count; j++)
            {
                if (!basinClosed[j])
                {
                    nOpen++;
                    continue;
                }
                List<int> cells = basins[j];
                cells.Sort((a, b) => z[a] != z[b] ? z[a].CompareTo(z[b]) : a.CompareTo(b));
                double left = basinIn[j];
                int kFlood = 0;
                while (kFlood < cells.Count && left >= cost[cells[kFlood]])
                {
                    left -= cost[cells[kFlood]];
                    kFlood++;
                }
                if (kFlood == 0)
                {
                    // 가장 낮은 칸 하나도 잠그지 못합니다: 마른 분지. 물길은 가장 낮은 칸에서 끝납니다.
                    sink[cells[0]] = true;
                    outlet[cells[0]] = true;
                    nDry++;
                    newSinks++;
                    continue;
                }
                // 수면: 마지막으로 잠긴 칸의 바닥과 다음 칸의 바닥 사이. 남은 물이 다음 칸을 잠그는 몫만큼 올립니다.
                double zLast = z[cells[kFlood - 1]];
                double h = zLast;
                if (kFlood < cells.Count)
                {
                    double zNext = z[cells[kFlood]];
                    double share = cost[cells[kFlood]] > 0.0 ? left / cost[cells[kFlood]] : 0.0;
                    h = zLast + (Math.Min(Math.Max(share, 0.0), 1.0) * (zNext - zLast));
                }
                bool any = false;
                for (int i = 0; i < kFlood; i++)
                {
                    int c = cells[i];
                    sink[c] = true;
                    outlet[c] = true;
                    newSinks++;
                    if (h - z[c] > PondedM)
                    {
                        closed[c] = true;
                        level[c] = h;
                        any = true;
                    }
                }
                if (any)
                {
                    nClosed++;
                }
                else
                {
                    nDry++;
                }
            }
            if (newSinks == 0)
            {
                break;
            }
        }
        return new LakeBalanceResult(level, closed, sink, nOpen, nClosed, nDry, passes);
    }
}
