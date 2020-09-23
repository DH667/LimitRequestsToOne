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
        public async Task LimiterShouldPreventMultipleExecution()
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

            var firstSet = Enumerable.Range(0, 5)
                .Select(n => limiter.Add(key, token => DoSomeStuff(currentValue), CancellationToken.None))
                .ToArray();

            limiter.TryInvalidate(key);

            var secondSet = Enumerable.Range(0, 5)
                .Select(n => limiter.Add(key, token => DoSomeStuff(currentValue), CancellationToken.None))
                .ToArray();

            var result = await Task.WhenAll(firstSet.Concat(secondSet));

            Assert.AreEqual(expectedValue, currentValue.Value);
            CollectionAssert.AreEqual(expected, result);
        }

        [Test]
        public async Task LimiterShouldInvalidateAllAwaiters()
        {
            var expectedValue = 3;
            var expected1 = Enumerable.Range(0, 10).Select(n => n < 5 ? 1 : 2).ToArray();
            var expected2 = Enumerable.Range(0, 10).Select(_ => 3).ToArray();
            var currentValue = new RefWrapper<int>(0);

            var limiter = new FutureLimiter<string, int>();

            var set1 = Enumerable.Range(0, 10)
                    .Select(n =>
                       {
                           var task = limiter.Add(key, token => DoSomeStuff(currentValue, 2000), CancellationToken.None);
                           if (n == 4)
                               limiter.InvalidateAll();
                           return task;
                       })
                    .ToArray();

            var set2 = Enumerable.Range(0, 10)
                .Select(n => limiter.Add("otherKey", token => DoSomeStuff(currentValue, 5000), CancellationToken.None))
                .ToArray();

            var result1 = await Task.WhenAll(set1);
            var result2 = await Task.WhenAll(set2);

            Assert.AreEqual(expectedValue, currentValue.Value);
            CollectionAssert.AreEqual(expected1, result1);
            CollectionAssert.AreEqual(expected2, result2);
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
                    .Select(_ => limiter.Add(key, token => DoSomeStuff(currentValue), CancellationToken.None))
                    .ToArray());

            var second = await Task.WhenAll(
                expected
                    .Skip(10)
                    .Select(_ => limiter.Add(key, token => DoSomeStuff(currentValue), CancellationToken.None))
                    .ToArray());

            var result = first.Concat(second);

            Assert.AreEqual(expectedValue, currentValue.Value);
            CollectionAssert.AreEqual(expected, result);
        }

        [Test]
        public async Task LimiterShouldPreventMultipleExecution2()
        {
            var expected = Enumerable.Range(0, 10).Select(_ => 1)
                .Concat(Enumerable.Range(0, 10).Select(_ => 1)).ToArray();

            var currentValue = new RefWrapper<int>(0);

            var limiter = new FutureLimiter<string, int>();

            var results = await Task.WhenAll(
                expected
                    .Take(10)
                    .Select(_ => limiter.Add(key,
                        token => DoSomeStuff(currentValue, token: token), CancellationToken.None))
                    .Concat(expected.Skip(10)
                        .Select(_ => limiter.Add(key,
                            token => DoSomeStuff(currentValue, token: token), CancellationToken.None)))
                   .ToArray());

            CollectionAssert.AreEqual(expected, results);
        }

        [Test]
        public void LimiterShouldReturnOriginalException()
        {
            Assert.ThrowsAsync(
                Is.InstanceOf<NullReferenceException>(),
                () => new FutureLimiter<string, int>().Add(
                    key,
                    async token => await DoSomeStuff(null, token: token),
                    CancellationToken.None));
        }

        [Test]
        public void LimiterShouldHandleCancelledTask()
        {
            var cts = new CancellationTokenSource();
            var limiter = new FutureLimiter<string, int>();
            var currentValue = new RefWrapper<int>(0);

            var awaiter = limiter.Add(key, async token =>
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

            var expected = Enumerable.Range(0, 10).Select(n => n == 5 ? -1 : 1).ToArray();

            var limiter = new FutureLimiter<string, int>();

            var awaiters = Enumerable.Range(0, 10)
                .Select(n =>
                    {
                        var x = n;
                        return limiter.Add(
                            key,
                            token => DoSomeStuff(currentValue, token: token),
                                x == 5 ? cts.Token : CancellationToken.None);
                    })
                .ToArray();

            await Task.Delay(TimeSpan.FromSeconds(1));
            cts.Cancel();

            var result = await AwaitResults<int>(awaiters);

            Assert.AreEqual(1, currentValue.Value);
            CollectionAssert.AreEqual(expected.Take(4), result.Take(4).Select(r => r.result));
            CollectionAssert.AreEqual(expected.Skip(6), result.Skip(6).Select(r => r.result));
            Assert.IsInstanceOf<TaskCanceledException>(result[5].exception);
        }

        private Task<(TResult result, Exception exception)[]> AwaitResults<TResult>(Task<TResult>[] tasks)
        =>
        Task.WhenAll(tasks.Select(t =>
            t.ContinueWith<(TResult, Exception)>(t =>
                  t.Status switch
                  {
                      TaskStatus.Faulted => (default, t.Exception.InnerException),
                      TaskStatus.Canceled => (default, new TaskCanceledException()),
                      _ => (t.Result, null)
                  })));

        private async Task<int> DoSomeStuff(
            RefWrapper<int> currentValue,
            int delayInMillis = 3000,
            CancellationToken token = default(CancellationToken))
        {
            await Task.Delay(delayInMillis, token);
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