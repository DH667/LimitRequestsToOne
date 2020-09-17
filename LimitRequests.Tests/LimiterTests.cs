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
                    .Select(_ => limiter.DoLimit("myStuff", () => DoSomeStuff(currentValue)))
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
                    .Select(_ => limiter.DoLimit("myStuff", () => DoSomeStuff(currentValue)))
                    .Concat(
                        expected.Skip(10)
                            .Select(_ => limiter.DoLimit("myStuff", () => DoSomeStuff(currentValue))))
                   .ToArray());

            CollectionAssert.AreEqual(expected, results);
        }

        [Test]
        [Ignore("Implementation not in place")]
        public async Task ShouldNotUseCancellationTokenWhenOthersAreWaitingForSameResult()
        {
            var expected = Enumerable.Range(0, 10).Select(_ => 1).ToArray();
            var currentValue = new RefWrapper<int>(0);

            var limiter = new Limiter<int>();

            var firstResults = await Task.WhenAll(
                expected
                    .Select(_ => limiter.DoLimit("myStuff", () => DoSomeStuff(currentValue)))
                    .ToArray());

            Assert.True(false);
        }

        async Task<int> DoSomeStuff(
            RefWrapper<int> currentValue,
            CancellationToken token = default(CancellationToken))
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
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