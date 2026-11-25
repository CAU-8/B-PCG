using Bpcg.Numerics;
using Xunit;

namespace Bpcg.Tests.Numerics;

/// <summary>
/// numpy 의 tanh 를 옮긴 <see cref="NpUfunc.Tanh"/> 대조. 기후의 유출 식(Budyko)이 이 함수를 쓰므로,
/// 마지막 비트가 달라지면 같은 시드의 행성이 달라집니다.
/// </summary>
public sealed class NpUfuncTests
{
    [Fact]
    public void TanhMatchesNumpyBitForBit()
    {
        GoldenCase g = Golden.Load("numerics/tanh", "vectors");
        double[] x = g.In("x").AsDouble();
        double[] got = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            got[i] = NpUfunc.Tanh(x[i]);
        }
        Compare.Bits(g.Out("tanh").AsDouble(), got, "tanh");
    }
}
