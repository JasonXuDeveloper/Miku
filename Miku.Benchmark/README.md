# Miku.Benchmark

This project contains benchmarks for the Miku library to measure its performance.

## How to Run

To run the benchmarks, execute the following command from the root of the repository:

```bash
dotnet run -c Release --project Miku.Benchmark/Miku.Benchmark.csproj
```

The results will be displayed in the console and also saved in the `BenchmarkDotNet.Artifacts` directory. 