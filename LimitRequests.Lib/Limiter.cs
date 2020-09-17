using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace LimitRequests.Lib
{
    public class Limiter<T>
    {
        public ConcurrentDictionary<string, Task<T>> actions = new ConcurrentDictionary<string, Task<T>>();

        public Task<T> DoLimit(string key, Func<Task<T>> func)
        {
            if (actions.ContainsKey(key))
                return actions[key];

            var value = func()
                .ContinueWith(t =>
                {
                    actions.TryRemove(key, out _);
                    return t.Result;
                });

            actions.TryAdd(key, value);
            return value;
        }
    }
}