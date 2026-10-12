# Compute kernel benchmark

This optional development tool compares the same finite, non-negative element-wise field transform on scalar CPU, SIMD CPU, CPU parallel + SIMD, and a detected ILGPU accelerator. GPU measurements include buffer allocation/disposal, host-to-device copies, synchronization, and device-to-host copy. Accelerator creation and JIT compilation are reported separately as startup cost.

Run on the lab-sized field:

```powershell
dotnet run --project tools/ComputeBench/ComputeBench.csproj -c Release -- --size 6291456 --repeats 7
```

The tool depends on ILGPU only; the generator/Core and Godot projects do not. If no usable accelerator or driver is available, the CPU measurements still complete and the report records a CPU fallback. GPU results are checked against scalar output before being reported. This is a feasibility probe, not a generation backend selection: integrate a GPU kernel into generation only after a transfer-inclusive benchmark shows a benefit.
