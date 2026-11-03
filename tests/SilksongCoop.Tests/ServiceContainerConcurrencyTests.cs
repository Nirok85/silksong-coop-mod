using System.Threading;
using SilksongCoop.Core.DI;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Regression tests for concurrent singleton resolution (3.0.1).
    /// </summary>
    public class ServiceContainerConcurrencyTests
    {
        private interface ICounterService
        {
        }

        private sealed class CounterService : ICounterService
        {
        }

        [Fact]
        public void SingletonFactory_IsCreatedOnceUnderConcurrentResolve()
        {
            var container = new ServiceContainer();
            int created = 0;

            container.RegisterSingleton<ICounterService>(_ =>
            {
                Interlocked.Increment(ref created);
                // Widen the race window; without the lock every worker can enter the factory.
                Thread.Sleep(25);
                return new CounterService();
            });

            const int WorkerCount = 8;
            var results = new ICounterService?[WorkerCount];
            var start = new ManualResetEventSlim(false);
            var threads = new Thread[WorkerCount];

            for (int i = 0; i < WorkerCount; i++)
            {
                int index = i;
                threads[i] = new Thread(() =>
                {
                    start.Wait();
                    results[index] = container.Resolve<ICounterService>();
                });
                threads[i].Start();
            }

            start.Set();
            for (int i = 0; i < threads.Length; i++)
                threads[i].Join();

            Assert.Equal(1, created);
            var first = results[0];
            Assert.NotNull(first);
            for (int i = 1; i < results.Length; i++)
                Assert.Same(first, results[i]);
        }
    }
}
