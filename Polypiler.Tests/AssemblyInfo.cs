using NUnit.Framework;

// Every fixture and every case runs in parallel, one worker per logical processor. Each case gets its
// own fixture instance, so the per-test directories and fields a [SetUp] creates are never shared.
[assembly: Parallelizable(ParallelScope.All)]
[assembly: FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
