using System;
using NUnit.Framework;

[assembly: CLSCompliant(false)]

// Every fixture and every case runs in parallel, one worker per logical processor. Each case gets its
// own fixture instance, so the working directory a [SetUp] creates is never shared.
[assembly: Parallelizable(ParallelScope.All)]
[assembly: FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
