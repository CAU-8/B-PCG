using System.Runtime.CompilerServices;

namespace Bpcg.Core;

/// <summary>
/// 결정적 정수 해시 (src/bpcg/core/hashing.py). 지형 모양에 영향을 주는 값은 모두 여기 해시로 만듭니다.
/// </summary>
/// <remarks>
/// 덧셈·곱셈은 넘친 비트를 버리는 ulong 연산이고(<c>unchecked</c>), 시프트는 논리 시프트입니다.
/// 정수를 ulong 으로 바꿀 때는 비트를 그대로 다시 읽습니다(numba <c>np.uint64(int64)</c> 와 같음).
/// </remarks>
public static class Hashing
{
    private const ulong M1 = 0xBF58476D1CE4E5B9UL;
    private const ulong M2 = 0x94D049BB133111EBUL;
    private const ulong Gold = 0x9E3779B97F4A7C15UL;
    private const double Inv53 = 1.0 / 9007199254740992.0; // 2^-53

    /// <summary>splitmix64 섞기 한 번.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong SplitMix64(ulong x)
    {
        unchecked
        {
            ulong z = x + Gold;
            z = (z ^ (z >> 30)) * M1;
            z = (z ^ (z >> 27)) * M2;
            return z ^ (z >> 31);
        }
    }

    /// <summary>정수 세 개를 64비트 해시 하나로 섞습니다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Hash3(long a, long b, long c)
    {
        unchecked
        {
            ulong h = SplitMix64((ulong)a);
            h = SplitMix64(h ^ (ulong)b);
            return SplitMix64(h ^ (ulong)c);
        }
    }

    /// <summary>정수 세 개 → [0, 1) 균등 실수. 위 53비트를 그대로 2^-53 배 합니다(정확한 변환).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double HashUnit(long a, long b, long c) => (double)(Hash3(a, b, c) >> 11) * Inv53;

    /// <summary>셀 번호 배열마다 [0, 1) 균등 실수 하나. stream 으로 용도를 나눕니다.</summary>
    public static double[] HashUniformArray(long[] ids, long seed, long stream)
    {
        double[] output = new double[ids.Length];
        for (int k = 0; k < ids.Length; k++)
        {
            output[k] = HashUnit(ids[k], seed, stream);
        }
        return output;
    }
}
