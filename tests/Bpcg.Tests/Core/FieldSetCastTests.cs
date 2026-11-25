using System;
using Bpcg.Core;
using Xunit;

namespace Bpcg.Tests.Core;

/// <summary>
/// 필드 배열의 형 바꾸기(<see cref="FieldSet.CastTo"/>)와 골라 담기(<see cref="FieldSet.Take"/>)가 원소 형을 정확히 가리는지.
/// .NET 런타임에서는 sbyte[] 가 byte[] 로도, int[] 가 uint[] 로도 판정됩니다(`(object)new sbyte[1] is byte[]` 가 참).
/// 형을 `is` 로 고르면 부호 있는 배열의 −1 이 255 로 읽힙니다. 섭입 방향 필드 subduction_side(−1·0·1, int8)가 이 경우입니다.
/// </summary>
public sealed class FieldSetCastTests
{
    [Fact]
    public void RuntimeTreatsSignedAndUnsignedArraysAsEachOther()
    {
        // 이 시험은 위 성질이 실제로 있다는 것을 적어 둡니다 (아래 두 시험이 왜 필요한지).
        object signed = new sbyte[] { -1 };
        Assert.True(signed is byte[]);
    }

    [Fact]
    public void TakeKeepsTheElementType()
    {
        Array signed = FieldSet.Take(new sbyte[] { -1, 0, 1 }, [0, 2, 2]);
        Assert.Equal(typeof(sbyte[]), signed.GetType());
        Assert.Equal(new sbyte[] { -1, 1, 1 }, (sbyte[])signed);

        Assert.Equal(typeof(byte[]), FieldSet.Take(new byte[] { 255, 0 }, [1, 0]).GetType());
        Assert.Equal(typeof(uint[]), FieldSet.Take(new uint[] { 4_000_000_000 }, [0]).GetType());
        Assert.Equal(typeof(int[]), FieldSet.Take(new[] { -5 }, [0]).GetType());
        Assert.Equal(typeof(ushort[]), FieldSet.Take(new ushort[] { 65_535 }, [0]).GetType());
        Assert.Equal(typeof(ulong[]), FieldSet.Take(new[] { ulong.MaxValue }, [0]).GetType());
    }

    [Fact]
    public void CastToReadsSignedAndUnsignedValuesAsWhatTheyAre()
    {
        Assert.Equal(new[] { -1.0, 0.0, 1.0 }, (double[])FieldSet.CastTo(new sbyte[] { -1, 0, 1 }, typeof(double)));
        Assert.Equal(new[] { -1, 1 }, (int[])FieldSet.CastTo(new sbyte[] { -1, 1 }, typeof(int)));
        Assert.Equal(new[] { 255.0 }, (double[])FieldSet.CastTo(new byte[] { 255 }, typeof(double)));
        Assert.Equal(new[] { 65_535 }, (int[])FieldSet.CastTo(new ushort[] { 65_535 }, typeof(int)));
        Assert.Equal(new[] { -1 }, (int[])FieldSet.CastTo(new short[] { -1 }, typeof(int)));
        Assert.Equal(new[] { 4_000_000_000L }, (long[])FieldSet.CastTo(new uint[] { 4_000_000_000 }, typeof(long)));
        Assert.Equal(new[] { -1L }, (long[])FieldSet.CastTo(new[] { -1 }, typeof(long)));
        // 같은 크기의 부호만 다른 형으로는 비트를 그대로 둡니다 (numpy astype 과 같음).
        Assert.Equal(new sbyte[] { -1 }, (sbyte[])FieldSet.CastTo(new byte[] { 255 }, typeof(sbyte)));
        Assert.Equal(new byte[] { 255 }, (byte[])FieldSet.CastTo(new sbyte[] { -1 }, typeof(byte)));
    }
}
