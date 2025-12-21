using Xunit;

// Disable test parallelization for the entire assembly on Framework
#if NETFRAMEWORK
[assembly: CollectionBehavior(DisableTestParallelization = true)]
#endif