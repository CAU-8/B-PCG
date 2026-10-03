using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Bpcg.IO;
using Xunit;

namespace Bpcg.Tests.IO;

/// <summary>.npy·.npz·JSON 형식이 Python(numpy 2.5.3, json)과 바이트·글자까지 같은지 봅니다.</summary>
public sealed class IoTests
{
    [Fact]
    public void NpyBytesMatchNpSave()
    {
        GoldenCase g = Golden.Load("io/npy", "save_bytes");
        foreach (JsonElement n in g.Meta.GetProperty("names").EnumerateArray())
        {
            string name = n.GetString()!;
            NpyArray input = g.In(name);
            byte[] expected = g.Out(name).AsByte();
            var ms = new MemoryStream();
            Npy.Write(ms, input.Data, input.Shape);
            Compare.Equal(expected, ms.ToArray(), $"npy 바이트 {name}");

            NpyArray back = Npy.Read(new MemoryStream(expected));
            Assert.Equal(input.Descr, back.Descr);
            Assert.Equal(input.Shape, back.Shape);
            Assert.Equal(input.Data.Length, back.Data.Length);
        }
    }

    [Fact]
    public void NpzBytesMatchNpSavez()
    {
        GoldenCase g = Golden.Load("io/npz", "savez_bytes");
        NpyArray pos = g.In("pos");
        NpyArray nbr = g.In("nbr");
        var ms = new MemoryStream();
        Npz.Write(ms, [new NpzEntry("pos", pos.Data, pos.Shape), new NpzEntry("nbr", nbr.Data, nbr.Shape)]);
        Compare.Equal(g.Out("bytes").AsByte(), ms.ToArray(), "npz 바이트");

        OrderedDictionary<string, NpyArray> back = Npz.Read(new MemoryStream(ms.ToArray()));
        Assert.Equal(["pos", "nbr"], back.Keys.ToArray());
        Compare.Bits(pos.AsDouble(), back["pos"].AsDouble(), "npz pos 되읽기");
        Compare.Equal(nbr.AsInt(), back["nbr"].AsInt(), "npz nbr 되읽기");
    }

    [Fact]
    public void FloatReprMatchesPython()
    {
        GoldenCase g = Golden.Load("io/pyjson", "float_repr");
        double[] values = g.In("values").AsDouble();
        string[] reprs = g.Meta.GetProperty("reprs").EnumerateArray().Select(e => e.GetString()!).ToArray();
        int bad = 0;
        string firstBad = "";
        for (int i = 0; i < values.Length; i++)
        {
            string got = PyJson.Dumps(values[i], new PyJson.Options());
            if (got != reprs[i])
            {
                bad++;
                if (firstBad.Length == 0)
                {
                    firstBad = $"{reprs[i]} ≠ {got}";
                }
            }
        }
        Assert.True(bad == 0, $"실수 표기 {bad}/{values.Length} 개가 다릅니다. 첫 예: {firstBad}");

        float[] f32 = g.In("values_f32").AsFloat();
        string[] r32 = g.Meta.GetProperty("reprs_f32").EnumerateArray().Select(e => e.GetString()!).ToArray();
        for (int i = 0; i < f32.Length; i++)
        {
            Assert.Equal(r32[i], PyJson.Dumps(f32[i], new PyJson.Options()));
        }
    }

    [Fact]
    public void JsonLayoutMatchesPython()
    {
        GoldenCase g = Golden.Load("io/pyjson", "layout");
        JsonElement meta = g.Meta;
        object? sample = FromJson(meta.GetProperty("sample"));
        Assert.Equal(meta.GetProperty("indent2").GetString(),
            PyJson.Dumps(sample, new PyJson.Options(Indent: 2, EnsureAscii: false)));
        Assert.Equal(meta.GetProperty("sorted").GetString(),
            PyJson.Dumps(sample, new PyJson.Options(SortKeys: true, EnsureAscii: false)));
        Assert.Equal(meta.GetProperty("compact").GetString(),
            PyJson.Dumps(sample, new PyJson.Options(EnsureAscii: false)));
        Assert.Equal(meta.GetProperty("ascii").GetString(), PyJson.Dumps(sample, new PyJson.Options()));
        JsonElement special = meta.GetProperty("special");
        Assert.Equal(special.GetProperty("nan").GetString(), PyJson.Dumps(double.NaN, new PyJson.Options()));
        Assert.Equal(special.GetProperty("inf").GetString(), PyJson.Dumps(double.PositiveInfinity, new PyJson.Options()));
        Assert.Equal(special.GetProperty("-inf").GetString(),
            PyJson.Dumps(double.NegativeInfinity, new PyJson.Options()));
        Assert.Throws<ArgumentException>(() => PyJson.Dumps(double.NaN, new PyJson.Options(AllowNan: false)));
    }

    /// <summary>시험용: System.Text.Json 값 → PyJson 값 모델 (숫자 글자에 '.'·'e' 가 있으면 double).</summary>
    internal static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => new OrderedDictionary<string, object?>(
            e.EnumerateObject().Select(p => new KeyValuePair<string, object?>(p.Name, FromJson(p.Value)))),
        JsonValueKind.Array => e.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Number => e.GetRawText().IndexOfAny(['.', 'e', 'E']) >= 0
            ? e.GetDouble()
            : (object)e.GetInt64(),
        _ => throw new ArgumentException($"알 수 없는 JSON 값: {e.ValueKind}"),
    };
}
