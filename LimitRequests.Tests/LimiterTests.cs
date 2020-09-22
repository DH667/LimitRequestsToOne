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
        private const string key = "myStuff";

        [Test]
        public async Task LimiterShouldPreventMultipleInvokation()
        {
            var expectedValue = 1;
            var expected = Enumerable.Range(0, 10).Select(_ => 1).ToArray();
            var currentValue = new RefWrapper<int>(0);

            var limiter = new FutureLimiter<string, int>();

            var result = await Task.WhenAll(
                expected
                    .Select(_ => limiter.Add(key, token => DoSomeStuff(currentValue), CancellationToken.None))
                    .ToArray());

            Assert.AreEqual(expectedValue, currentValue.Value);
            CollectionAssert.AreEqual(expected, result);
        }

        [Test]
        public async Task LimiterShouldAllowToForceNewSetOfAwaiters()
        {
            var expectedValue = 2;
            var expected = Enumerable.Range(0, 10).Select(n => n / 5 + 1).ToArray();
            var currentValue = new RefWrapper<int>(0);

            var limiter = new FutureLimiter<string, int>();

            var result = await Task.WhenAll(
                Enumerable.Range(0, 10)
                    .Select(n =>
                       {
                           var task = limiter.Add(key, token => DoSomeStuff(currentValue), CancellationToken.None);
                           if (n == 4)
                               limiter.TryInvalidate(key);
                           return task;
                       })
                    .ToArray());

            Assert.AreEqual(expectedValue, currentValue.Value);
            CollectionAssert.AreEqual(expected, result);
        }

        [Test]
        public async Task LimiterShouldInvalidateAllAwaiters()
        {
            var expectedValue = 3;
            var expected = Enumerable.Range(0, 10).Select(n => n / 5 + 2).ToArray();
            var currentValue = new RefWrapper<int>(0);

            var limiter = new FutureLimiter<string, int>();

            var set1 = Enumerable.Range(0, 10)
                    .Select(n =>
                       {
                           var task = limiter.Add(key, token => DoSomeStuff(currentValue), CancellationToken.None);
                           if (n == 4)
                               limiter.InvalidateAll();
                           return task;
                       })
                    .ToArray();
            var set2 = Enumerable.Range(0, 10)
            .Select(n => limiter.Add("otherKey", token => DoSomeStuff(currentValue), CancellationToken.None))
            .ToArray();

            var result1 = await Task.WhenAll(set1);
            var result2 = await Task.WhenAll(set2);

            Assert.AreEqual(expectedValue, currentValue.Value);
            CollectionAssert.AreEqual(expected, result1);
            CollectionAssert.AreEqual(expected, result1);
        }

        [Test]
        public async Task LimiterShouldNotReuseCompletedAction()
        {
            var expected = Enumerable.Range(0, 20).Select(n => n / 10 + 1).ToArray();
            var currentValue = new RefWrapper<int>(0);
            var expectedValue = 2;

            var limiter = new FutureLimiter<string, int>();

            var first = await Task.WhenAll(
                expected
                    .Take(10)
                    .Select(_ => limiter.Add("myStuff", token => DoSomeStuff(currentValue), CancellationToken.None))
                    .ToArray());

            var second = await Task.WhenAll(
                expected
                    .Skip(10)
                    .Select(_ => limiter.Add("myStuff", token => DoSomeStuff(currentValue), CancellationToken.None))
                    .ToArray());

            var result = first.Concat(second);

            Assert.AreEqual(expectedValue, currentValue.Value);
            CollectionAssert.AreEqual(expected, result);
        }

        [Test]
        public async Task LimiterShouldPreventMultipleInvokation2()
        {
            var expected = Enumerable.Range(0, 10).Select(_ => 1)
                .Concat(Enumerable.Range(0, 10).Select(_ => 1)).ToArray();

            var currentValue = new RefWrapper<int>(0);

            var limiter = new FutureLimiter<string, int>();

            var results = await Task.WhenAll(
                expected
                    .Take(10)
                    .Select(_ => limiter.Add("myStuff",
                        token => DoSomeStuff(currentValue, token), CancellationToken.None))
                    .Concat(expected.Skip(10)
                        .Select(_ => limiter.Add("myStuff",
                            token => DoSomeStuff(currentValue, token), CancellationToken.None)))
                   .ToArray());

            CollectionAssert.AreEqual(expected, results);
        }

        [Test]
        public void LimiterShouldReturnOriginalException()
        {
            Assert.ThrowsAsync(
                Is.InstanceOf<NullReferenceException>(),
                () => new FutureLimiter<string, int>().Add("key", async token => await DoSomeStuff(null, token), CancellationToken.None));
        }

        [Test]
        public void LimiterShouldHandleCancelledTask()
        {
            var cts = new CancellationTokenSource();
            var limiter = new FutureLimiter<string, int>();
            var currentValue = new RefWrapper<int>(0);

            var awaiter = limiter.Add("key", async token =>
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

            var limiter = new FutureLimiter<string, int>();

            var awaiters = Enumerable.Range(0, 10)
                    .Select(n => limiter.Add("myStuff", token => DoSomeStuff(currentValue, token), tokens[n]))
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

        private class RefWrapper<T> where T : struct
        {
            public RefWrapper(T value)
            {
                Value = value;
            }

            public T Value;
        }
    }
}