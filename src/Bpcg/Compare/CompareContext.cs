using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Bpcg.Compare;

/// <summary>연산 과정의 중간 지도 한 장. Data: (n·n,) float32.</summary>
public sealed record StageSnapshot(string Label, string Kind, string Note, string Unit, string Scale, float[] Data);

/// <summary>
/// 생성 함수가 바깥 자원을 쓰거나 연산 과정을 남길 때 거치는 곳. 실행기가 만듭니다.
/// </summary>
/// <remarks>
/// 과정 기록은 <see cref="Recording"/> 일 때만 남습니다.
/// <see cref="Snapshot"/>: 중간 지도 한 장 (kind 는 <see cref="StageKinds"/>, scale "final" 이면 고도 그림의 색 범위를 최종 고도에 맞춤).
/// <see cref="Trace"/>: 반복마다 바뀐 값의 곡선. <see cref="Bar"/>: 항목별 막대.
/// <see cref="Extra"/>: 기록만을 위한 추가 계산. 그 시간은 생성 시간에서 뺍니다.
/// 기록을 켜도 고도는 비트까지 같아야 합니다(시험).
/// </remarks>
public sealed class CompareContext
{
    /// <summary>단계 종류 → 한국어 이름.</summary>
    public static readonly OrderedDictionary<string, string> StageKinds = new()
    {
        ["height"] = "고도 (음영 기복)",
        ["field"] = "값 (연속 색)",
        ["log"] = "값 (로그 색)",
        ["diverging"] = "증감 (0 기준 두 색)",
        ["mask"] = "표시 (고도 위에 칠함)",
        ["category"] = "종류 (번호마다 색)",
    };

    private long _extraTicks;
    private int _extraDepth; // Extra 안의 Snapshot 시간을 두 번 세지 않게

    public bool Recording { get; init; }

    public Action<string>? Log { get; init; }

    /// <summary>예제 지형 읽기: 이름 → (고도 (ny·nx,) [m], ny, nx, 칸 간격 [m]).</summary>
    public Func<string, (double[] Z, int Ny, int Nx, double Dx)>? ExemplarLoader { get; init; }

    public List<StageSnapshot> Stages { get; } = [];

    public List<OrderedDictionary<string, object?>> Series { get; } = [];

    public List<OrderedDictionary<string, object?>> Bars { get; } = [];

    /// <summary>기록만을 위해 쓴 시간 [s].</summary>
    public double ExtraSeconds => (double)_extraTicks / Stopwatch.Frequency;

    public void Say(string msg) => Log?.Invoke(msg);

    public (double[] Z, int Ny, int Nx, double Dx) Exemplar(string name) =>
        ExemplarLoader is null
            ? throw new InvalidOperationException("예제 지형을 읽을 수 없습니다 (ExemplarLoader 없음)")
            : ExemplarLoader(name);

    /// <summary>기록용 추가 계산을 감쌉니다: <c>using (ctx.Extra()) { ... }</c>.</summary>
    public IDisposable Extra() => new ExtraTimer(this);

    public void Snapshot(string label, double[] data, string kind = "height", string note = "", string unit = "", string scale = "self")
    {
        if (!Recording)
        {
            return;
        }
        if (!StageKinds.ContainsKey(kind))
        {
            throw new ArgumentException($"snapshot kind 는 [{string.Join(", ", StageKinds.Keys)}] 가운데 하나: '{kind}'");
        }
        using (Extra())
        {
            float[] copy = Array.ConvertAll(data, v => (float)v);
            Stages.Add(new StageSnapshot(label, kind, note, unit, scale, copy));
        }
    }

    public void Trace(string name, IEnumerable<double> x, IEnumerable<double> y, string label = "", string unit = "", string xlabel = "")
    {
        if (!Recording)
        {
            return;
        }
        List<object?> xs = x.Select(v => (object?)v).ToList();
        List<object?> ys = y.Select(v => (object?)v).ToList();
        if (xs.Count != ys.Count)
        {
            throw new ArgumentException($"trace {name}: x, y 길이가 다릅니다");
        }
        Series.Add(new OrderedDictionary<string, object?>
        {
            ["name"] = name,
            ["label"] = label.Length > 0 ? label : name,
            ["unit"] = unit,
            ["xlabel"] = xlabel,
            ["x"] = xs,
            ["y"] = ys,
        });
    }

    public void Bar(string name, IEnumerable<(string Label, double Value)> items, string label = "", string unit = "")
    {
        if (!Recording)
        {
            return;
        }
        Bars.Add(new OrderedDictionary<string, object?>
        {
            ["name"] = name,
            ["label"] = label.Length > 0 ? label : name,
            ["unit"] = unit,
            ["items"] = items.Select(it => (object?)new OrderedDictionary<string, object?> { ["label"] = it.Label, ["value"] = it.Value }).ToList(),
        });
    }

    private sealed class ExtraTimer : IDisposable
    {
        private readonly CompareContext _ctx;
        private readonly long _t0;

        public ExtraTimer(CompareContext ctx)
        {
            _ctx = ctx;
            _ctx._extraDepth++;
            _t0 = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            _ctx._extraDepth--;
            if (_ctx._extraDepth == 0)
            {
                _ctx._extraTicks += Stopwatch.GetTimestamp() - _t0;
            }
        }
    }
}
