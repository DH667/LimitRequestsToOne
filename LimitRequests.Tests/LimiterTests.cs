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

            var firstResults = await Task.WhenAll(
                expected
                    .Select(_ => limiter.DoLimit("myStuff", token => DoSomeStuff(currentValue), CancellationToken.None))
                    .ToArray());

            CollectionAssert.AreEqual(expected, firstResults);
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
        public async Task LimiterSholdPreserveOriginalStackTrace()
        {
            var limiter = new Limiter<int>();

            Exception expected = null;
            try
            {
                await DoSomeStuff(null, CancellationToken.None);
            }
            catch (Exception e)
            {
                expected = e;
            }

            Exception result = null;
            try
            {
                await limiter.DoLimit("key", token => DoSomeStuff(null, token), CancellationToken.None);
            }
            catch (Exception e)
            {
                result = e;
            }

            Assert.AreEqual(expected.GetType(), result.GetType());
        }

        [Test]
        public async Task LimiterShouldHandleCancelledTask()
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
        [Ignore("Does not work as supposed")]
        public async Task ShouldCancelOnlyOneTask()
        {
            var cts = new CancellationTokenSource();
            var tokens = Enumerable.Range(0, 10).Select(n => n % 5 == 0 ? cts.Token : CancellationToken.None).ToArray();
            var expected = Enumerable.Range(0, 10).Select(_ => 1).ToArray();
            var currentValue = new RefWrapper<int>(0);

            var limiter = new Limiter<int>();

            var firstResults = await Task.WhenAll(
                Enumerable.Range(0, 10)
                    .Select(n => limiter.DoLimit("myStuff", token => DoSomeStuff(currentValue, token), tokens[n]))
                    .ToArray());

            cts.CancelAfter(1000);

            Assert.True(false);
        }

        async Task<int> DoSomeStuff(
            RefWrapper<int> currentValue,
            CancellationToken token = default(CancellationToken))
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            return Interlocked.Increment(ref currentValue.Item);
        }

        class RefWrapper<T> where T : struct
        {
            public RefWrapper(T item)
            {
                Item = item;
            }

            public T Item;
        }
    }
}