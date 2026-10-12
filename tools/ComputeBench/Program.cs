using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Bpcg.Core;
using ILGPU;
using ILGPU.Runtime;

namespace Bpcg.ComputeBench;

internal static class Program
{
    private const double Scale = 0.1;
    private const double Floor = 0.01;

    private static void MaxProductFloorKernel(
        Index1D index, ArrayView<double> primary, ArrayView<double> secondary,
        ArrayView<double> output, double scale, double floor)
    {
        double value = primary[index];
        double product = scale * secondary[index];
        if (product > value)
        {
            value = product;
        }
        if (floor > value)
        {
            value = floor;
        }
        output[index] = value;
    }

    private sealed record Measurement(string Name, double MedianMs, double MinMs, double MaxMs,
        double CpuUtilizationPercent, long AllocatedBytes, double Speedup);

    private static double Median(double[] values)
    {
        Array.Sort(values);
        int middle = values.Length / 2;
        return values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) * 0.5 : values[middle];
    }

    private static Measurement Measure(string name, Action action, int repeats, double baselineMs)
    {
        action();
        action();
        double[] samples = new double[repeats];
        Process process = Process.GetCurrentProcess();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 0; i < repeats; i++)
        {
            long start = Stopwatch.GetTimestamp();
            action();
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        TimeSpan cpu = process.TotalProcessorTime - cpuBefore;
        double median = Median((double[])samples.Clone());
        double wall = samples.Sum();
        double cpuUtilization = wall <= 0 ? 0 : cpu.TotalMilliseconds / wall / Environment.ProcessorCount * 100.0;
        return new Measurement(name, median, samples.Min(), samples.Max(), cpuUtilization,
            allocated, baselineMs <= 0 ? 1 : baselineMs / median);
    }

    private static double Difference(double[] expected, double[] actual)
    {
        double max = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            max = Math.Max(max, Math.Abs(expected[i] - actual[i]));
        }
        return max;
    }

    private static int PositiveArg(string[] args, string key, int fallback)
    {
        int position = Array.IndexOf(args, key);
        return position >= 0 && position + 1 < args.Length && int.TryParse(args[position + 1], out int value) && value > 0
            ? value : fallback;
    }

    public static int Main(string[] args)
    {
        int count = PositiveArg(args, "--size", 6_291_456);
        int repeats = PositiveArg(args, "--repeats", 7);
        double[] primary = new double[count];
        double[] secondary = new double[count];
        double[] expected = new double[count];
        double[] actual = new double[count];
        Random random = new(6226);
        for (int i = 0; i < count; i++)
        {
            primary[i] = random.NextDouble() * 5.0;
            secondary[i] = random.NextDouble() * 20.0;
        }

        Parallelism.Configure(1);
        Measurement scalar = Measure("CPU scalar", () =>
            FieldKernels.MaxProductFloor(primary, secondary, Scale, Floor, expected, useSimd: false), repeats, 0);
        Measurement simd = Measure("CPU SIMD", () =>
            FieldKernels.MaxProductFloor(primary, secondary, Scale, Floor, actual, useSimd: true), repeats, scalar.MedianMs);
        if (Difference(expected, actual) != 0)
        {
            throw new InvalidOperationException("SIMD 결과가 scalar 결과와 다릅니다");
        }

        Parallelism.Configure(Environment.ProcessorCount);
        Measurement parallel = Measure("CPU parallel + SIMD", () =>
            FieldKernels.MaxProductFloorParallel(primary, secondary, Scale, Floor, actual), repeats, scalar.MedianMs);
        if (Difference(expected, actual) != 0)
        {
            throw new InvalidOperationException("병렬 SIMD 결과가 scalar 결과와 다릅니다");
        }

        var measurements = new List<Measurement> { scalar, simd, parallel };
        string? gpuStatus = null;
        double? gpuMaxError = null;
        double? gpuSetupMs = null;
        try
        {
            long setupStart = Stopwatch.GetTimestamp();
            using Context context = Context.CreateDefault();
            Device device = context.GetPreferredDevice(preferCPU: false);
            using Accelerator accelerator = device.CreateAccelerator(context);
            var kernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>,
                ArrayView<double>, double, double>(MaxProductFloorKernel);
            gpuSetupMs = Stopwatch.GetElapsedTime(setupStart).TotalMilliseconds;
            gpuStatus = $"{accelerator.AcceleratorType}: {accelerator.Name}";

            Action gpuRun = () =>
            {
                using MemoryBuffer1D<double, Stride1D.Dense> gpuPrimary = accelerator.Allocate1D<double>(count);
                using MemoryBuffer1D<double, Stride1D.Dense> gpuSecondary = accelerator.Allocate1D<double>(count);
                using MemoryBuffer1D<double, Stride1D.Dense> gpuOutput = accelerator.Allocate1D<double>(count);
                gpuPrimary.CopyFromCPU(primary);
                gpuSecondary.CopyFromCPU(secondary);
                kernel(count, gpuPrimary.View, gpuSecondary.View, gpuOutput.View, Scale, Floor);
                accelerator.Synchronize();
                gpuOutput.CopyToCPU(actual);
            };
            Measurement gpu = Measure("GPU end-to-end (alloc + upload + kernel + sync + download)",
                gpuRun, repeats, scalar.MedianMs);
            gpuMaxError = Difference(expected, actual);
            if (gpuMaxError != 0)
            {
                throw new InvalidOperationException($"GPU 결과 오차: max abs = {gpuMaxError:R}");
            }
            measurements.Add(gpu);
        }
        catch (Exception e) when (gpuMaxError is null)
        {
            // Generation always has the CPU kernels. A missing driver/device must not fail this probe.
            gpuStatus = $"CPU fallback ({e.GetType().Name}: {e.Message})";
        }
        finally
        {
            Parallelism.Configure(null);
        }

        var report = new
        {
            cells = count,
            repeats,
            logicalProcessors = Environment.ProcessorCount,
            vector = new { Vector.IsHardwareAccelerated, doubleWidth = Vector<double>.Count },
            gpuStatus,
            gpuSetupMs,
            gpuMaxAbsError = gpuMaxError,
            measurements,
        };
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
