using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LimitRequests.Lib
{
    public class FutureLimiter<TKey, TResult>
    {
        private readonly ConcurrentDictionary<TKey, AwaitedItem> actions = new ConcurrentDictionary<TKey, AwaitedItem>();

        public Task<TResult> Add(TKey key, Func<CancellationToken, Task<TResult>> func, CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return Task.FromCanceled<TResult>(token);

            var tcs = new TaskCompletionSource<TResult>();

            if (actions.TryGetValue(key, out var value))
            {
                value.Increment(tcs);
            }
            else
            {
                value = new AwaitedItem(() => actions.Remove(key, out _), func, tcs);
                actions.TryAdd(key, value);
            }

            token.Register(() =>
                {
                    value.Decrement(tcs);
                    tcs.SetCanceled();
                });

            return tcs.Task;
        }

        public bool TryInvalidate(TKey key) => actions.TryRemove(key, out _);

        public void InvalidateAll() => actions.Clear();

        class AwaitedItem
        {
            private readonly object _lock = new object();
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private List<TaskCompletionSource<TResult>> _tcs = new List<TaskCompletionSource<TResult>>();

            public AwaitedItem(
                Action remove,
                Func<CancellationToken, Task<TResult>> task,
                TaskCompletionSource<TResult> tcs)
            {
                lock (_lock)
                {
                    _tcs.Add(_tcs.Count == 0
                        ? tcs
                        : throw new Exception($"Should never happen"));
                }

                task(_cts.Token).ContinueWith(t =>
                   {
                       remove();

                       switch (t.Status)
                       {
                           case TaskStatus.Faulted:
                               foreach (var tcs in _tcs)
                                   tcs.SetException(t.Exception.InnerException);
                               break;
                           case TaskStatus.Canceled:
                               _tcs.ForEach(tcs => tcs.SetCanceled());
                               break;
                           default:
                               foreach (var tcs in _tcs)
                                   if (!tcs.Task.IsCanceled)
                                       tcs.SetResult(t.Result);
                               break;
                       };
                   });
            }

            public void Increment(TaskCompletionSource<TResult> tcs)
            {
                lock (_lock)
                {
                    _tcs.Add(tcs);
                }
            }

            public void Decrement(TaskCompletionSource<TResult> tcs)
            {
                lock (_lock)
                {
                    _tcs.Remove(tcs);
                    if (_tcs.Count == 0)
                        _cts.Cancel();
                }
            }
        }
    }
}