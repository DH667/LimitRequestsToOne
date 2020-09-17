using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LimitRequests
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Start");
            int currentValue = 0;

            var limiter = new Limiter<int>();

            var firstResults = await Task.WhenAll(
                Enumerable.Range(0, 10)
                    .Select(_ => limiter.DoLimit("myStuff", DoSomeStuff))
                    .ToArray());

            var secondResults = await Task.WhenAll(
                Enumerable.Range(0, 10)
                    .Select(_ => limiter.DoLimit("myStuff", DoSomeStuff))
                    .ToArray());

            Console.WriteLine($"first set {string.Join(",", firstResults)}");
            Console.WriteLine($"second set {string.Join(",", secondResults)}");

            async Task<int> DoSomeStuff()
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                return Interlocked.Increment(ref currentValue);
            }
        }

        class Limiter<T>
        {
            public ConcurrentDictionary<string, Task<T>> actions = new ConcurrentDictionary<string, Task<T>>();

            public Task<T> DoLimit(string key, Func<Task<T>> func)
            {
                if (actions.ContainsKey(key))
                {
                    System.Console.WriteLine("using existing");
                    return actions[key];
                }

                var value = func()
                    .ContinueWith(t =>
                    {
                        actions.Remove(key, out _);
                        System.Console.WriteLine("removed");
                        return t.Result;
                    });

                Console.WriteLine("added");
                actions.TryAdd(key, value);
                return value;
            }
        }
    }
}