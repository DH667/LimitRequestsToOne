using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LimitRequests.Lib;

namespace LimitRequests.Runner
{
    partial class Program
    {
        static async Task Main(string[] args)
        {
            var cts = new CancellationTokenSource();
            Console.WriteLine("Start");
            int currentValue = 0;

            var limiter = new FutureLimiter<string, int>();

            var firstResults = await Task.WhenAll(
                Enumerable.Range(0, 10)
                    .Select(_ => limiter.Add("myStuff", token => DoSomeStuff(token), cts.Token))
                    .ToArray());

            var awaiters = Enumerable.Range(0, 10)
                   .Select(_ => limiter.Add("myStuff", token => DoSomeStuff(token), cts.Token))
                   .ToArray();

            var secondResults = await Task.WhenAll(awaiters);

            Console.WriteLine($"first set {string.Join(",", firstResults)}");
            Console.WriteLine($"second set {string.Join(",", secondResults)}");

            async Task<int> DoSomeStuff(CancellationToken token)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token);
                return Interlocked.Increment(ref currentValue);
            }
        }
    }
}