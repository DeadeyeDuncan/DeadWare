using System.Runtime.CompilerServices;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>
/// Several service tests deliberately park a thread-pool thread inside a worker callback (a store that waits
/// for a release signal, a hung native stop, a slow flush) while other classes run in parallel. On a four-core
/// runner the pool's default minimum of four threads then injects new threads at roughly one per half second,
/// so a continuation behind a one-second deadline can wait seconds and a timing assertion fails although the
/// code is correct. Raising the minimum lets the host hand out threads immediately, which is what production
/// sees on an idle desktop.
/// </summary>
internal static class TestHostThreadPool
{
    public const string TimingCollection = "Timing-sensitive services";

    [ModuleInitializer]
    internal static void Initialize()
    {
        ThreadPool.GetMinThreads(out int workers, out int completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), Math.Max(completionPorts, 64));
    }
}

/// <summary>Classes with wall-clock deadlines run one at a time so none of them measures another's blocking wait.</summary>
[CollectionDefinition(TestHostThreadPool.TimingCollection, DisableParallelization = true)]
public sealed class TimingSensitiveCollection { }
