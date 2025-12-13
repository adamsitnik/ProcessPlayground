using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;

var job = Job.Default
    .WithWarmupCount(1) // 1 warmup is enough for our purpose
    .WithIterationTime(TimeInterval.FromMilliseconds(250)) // the default is 0.5s per iteration, which is slighlty too much for us
    .WithMinIterationCount(15)
    .WithMaxIterationCount(20) // we don't want to run more that 20 iterations
    .AsDefault();

var config = ManualConfig.CreateMinimumViable()
    .AddJob(job)
    .AddExporter(MarkdownExporter.GitHub)
    .AddDiagnoser(MemoryDiagnoser.Default)
#if NET
    .AddDiagnoser(new ThreadingDiagnoser(new ThreadingDiagnoserConfig(displayLockContentionWhenZero: false)))
#endif
    .HideColumns(Column.Error, Column.StdDev, Column.RatioSD);

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
    .Run(args, config);
