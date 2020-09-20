using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using LimitRequests.Lib;

namespace LimitRequests.Benchmark
{
    class Program
    {
        static void Main(string[] args)
        {
            var summary = BenchmarkRunner.Run(typeof(Program).Assembly);
        }
    }

    [MemoryDiagnoser]
    public class Setup
    {
        private const int _length = 1000;
        private readonly byte[] _bytes = new byte[_length];
        private readonly Limiter<byte[]> _limiter = new Limiter<byte[]>();
        private readonly Random _random = new Random();

        [Benchmark]
        public async Task<byte[]> DoNormal()
        {
            var results = await Task.WhenAll(
                Enumerable.Range(0, _length)
                    .Select(async _ => {
                        await Task.Delay(TimeSpan.FromSeconds(1));
                        return _bytes;
                    })
                    .ToArray());

            return results[_random.Next(0, _length)];
        }

        [Benchmark]
        public async Task<byte[]> DoLimited()
        {
            var awaiters = await Task.WhenAll(
                Enumerable.Range(0, _length)
                    .Select(_ => _limiter.DoLimit(
                        "Benchmark",
                        async token =>
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1));
                            return _bytes;
                        },
                        CancellationToken.None))
                    .ToArray());

            return awaiters[_random.Next(0, _length)];
        }
    }
}
