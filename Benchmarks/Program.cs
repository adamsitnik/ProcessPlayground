// See https://aka.ms/new-console-template for more information
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

var config = ManualConfig.CreateMinimumViable()
    .AddJob(Job.ShortRun)
    .AddDiagnoser(MemoryDiagnoser.Default)
    .AddDiagnoser(ThreadingDiagnoser.Default);

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
    .Run(args, config);
