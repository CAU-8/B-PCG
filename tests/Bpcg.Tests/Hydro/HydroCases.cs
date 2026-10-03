using Xunit;

namespace Bpcg.Tests.Hydro;

/// <summary>hydro golden 사례 이름 (csharp/golden/export_golden.py export_hydro).</summary>
public static class HydroCases
{
    public static readonly TheoryData<string> Names = new() { "sphere32", "sphere7", "flat64", "flat20x30_ties" };
}
