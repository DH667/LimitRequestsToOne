using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace LimitRequests.Lib
{
    public class Limiter<T>
    {
        private readonly ConcurrentDictionary<string, AwaitedItem> actions = new ConcurrentDictionary<string, AwaitedItem>();

        public Task<T> DoLimit(string key, Func<CancellationToken, Task<T>> func, CancellationToken token)
        {
            if (actions.ContainsKey(key))
                return actions[key].Increment().Awaiter;

            var value = new AwaitedItem(() => actions.TryRemove(key, out _), func);
            token.Register(() => value.Decrement());

            actions.TryAdd(key, value);
            return value.Awaiter;
        }

        class AwaitedItem
        {
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private int _numberOfAwaiters = 0;
            private readonly Task<T> _task;

            public Task<T> Awaiter => _task;

            public AwaitedItem(
                Action remove,
                Func<CancellationToken,
                Task<T>> task)
            {
                if (Interlocked.Increment(ref _numberOfAwaiters) > 1)
                    throw new Exception("Should not happen");

                _task = task(_cts.Token).ContinueWith(t =>
                    {
                        remove();
                        return t.Status switch
                        {
                            TaskStatus.Faulted => throw t.Exception.InnerException,
                            TaskStatus.Canceled => throw new TaskCanceledException(),
                            _ => t.Result
                        };
                    });
            }

            public AwaitedItem Increment()
            {
                Interlocked.Increment(ref _numberOfAwaiters);
                return this;
            }

            public void Decrement()
            {
                if (Interlocked.Decrement(ref _numberOfAwaiters) == 0)
                {
                    _cts.Cancel();
                }
            }
        }
    }
}