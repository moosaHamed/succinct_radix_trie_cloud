using BenchmarkDotNet.Running;

// Pick benchmarks interactively, or pass --filter "*Select*" to run a subset
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
