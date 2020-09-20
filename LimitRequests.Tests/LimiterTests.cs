using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LimitRequests.Lib;
using NUnit.Framework;

namespace LimitRequests.Tests
{
    public class LimiterTests
    {
        [Test]
        public async Task LimiterShouldPreventMultipleInvokation()
        {
            var expected = Enumerable.Range(0, 10).Select(_ => 1).ToArray();
            var currentValue = new RefWrapper<int>(0);

            var limiter = new Limiter<int>();

            var result = await Task.WhenAll(
                expected
                    .Select(_ => limiter.DoLimit("myStuff", token => DoSomeStuff(currentValue), CancellationToken.None))
                    .ToArray());

            CollectionAssert.AreEqual(expected, result);
        }

        [Test]
        public async Task LimiterShouldPreventMultipleInvokation2()
        {
            var expected = Enumerable.Range(0, 10).Select(_ => 1)
                .Concat(Enumerable.Range(0, 10).Select(_ => 1)).ToArray();

            var currentValue = new RefWrapper<int>(0);

            var limiter = new Limiter<int>();

            var results = await Task.WhenAll(
                expected
                    .Take(10)
                    .Select(_ => limiter.DoLimit("myStuff",
                        token => DoSomeStuff(currentValue, token), CancellationToken.None))
                    .Concat(expected.Skip(10)
                        .Select(_ => limiter.DoLimit("myStuff",
                            token => DoSomeStuff(currentValue, token), CancellationToken.None)))
                   .ToArray());

            CollectionAssert.AreEqual(expected, results);
        }

        [Test]
        public void LimiterSholdPreserveOriginalStackTrace()
        {
            Assert.ThrowsAsync(
                Is.InstanceOf<NullReferenceException>(),
                () => new Limiter<int>().DoLimit("key", async token => await DoSomeStuff(null, token), CancellationToken.None));
        }

        [Test]
        public void LimiterShouldHandleCancelledTask()
        {
            var cts = new CancellationTokenSource();
            var limiter = new Limiter<int>();
            var currentValue = new RefWrapper<int>(0);

            var awaiter = limiter.DoLimit("key", async token =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), token);
                    return -1;
                },
            cts.Token);

            cts.CancelAfter(1000);

            Assert.ThrowsAsync(Is.InstanceOf<TaskCanceledException>(), async () => await awaiter);
        }

        [Test]
        public async Task ShouldCancelOnlyOneTask()
        {
            var cts = new CancellationTokenSource();
            var currentValue = new RefWrapper<int>(0);

            var tokens = Enumerable.Range(0, 10).Select(n => Predicate(n) ? cts.Token : CancellationToken.None).ToArray();
            var expected = Enumerable.Range(0, 10).Select(n => Predicate(n) ? -1 : 1).ToArray();

            var limiter = new Limiter<int>();

            var awaiters = Enumerable.Range(0, 10)
                    .Select(n => limiter.DoLimit("myStuff", token => DoSomeStuff(currentValue, token), tokens[n]))
                    .Select(t => t.ContinueWith<int>(t =>
                        t.Status == TaskStatus.Canceled
                        ? -1
                        : t.Result
                    ))
                    .ToArray();

            await Task.Delay(TimeSpan.FromSeconds(1));
            cts.Cancel();

            var result = await Task.WhenAll(awaiters);

            Assert.AreEqual(1, currentValue.Value);
            CollectionAssert.AreEqual(expected, result);

            static bool Predicate(int n) => n % 10 == 0;
        }

        async Task<int> DoSomeStuff(
            RefWrapper<int> currentValue,
            CancellationToken token = default(CancellationToken))
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            return Interlocked.Increment(ref currentValue.Value);
        }

        class RefWrapper<T> where T : struct
        {
            public RefWrapper(T value)
            {
                Value = value;
            }

            public T Value;
        }
    }
}