using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LimitRequests.Lib
{
    public class FutureLimiter<TKey, TResult>
    {
        private readonly ConcurrentDictionary<TKey, AwaitedItem> _futures = new ConcurrentDictionary<TKey, AwaitedItem>();

        public Task<TResult> Add(TKey key, Func<CancellationToken, Task<TResult>> func, CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return Task.FromCanceled<TResult>(token);

            var tcs = new TaskCompletionSource<TResult>();

            var value = _futures.AddOrUpdate(
                key,
                key => new AwaitedItem(key, _futures, func, tcs),
                (key, current) => current.Increment(tcs)
                );

            token.Register(() =>
                {
                    value.Decrement(tcs);
                    tcs.SetCanceled();
                });

            return tcs.Task;
        }

        public bool TryInvalidate(TKey key) => _futures.TryRemove(key, out _);

        public void InvalidateAll() => _futures.Clear();

        class AwaitedItem
        {
            private readonly object _lock = new object();
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private List<TaskCompletionSource<TResult>> _tcs = new List<TaskCompletionSource<TResult>>();

            public AwaitedItem(
                TKey key,
                ConcurrentDictionary<TKey, AwaitedItem> actions,
                Func<CancellationToken, Task<TResult>> task,
                TaskCompletionSource<TResult> tcs)
            {
                lock (_lock)
                    _tcs.Add(tcs);

                task(_cts.Token).ContinueWith(t =>
                   {
                       actions.TryRemove(key, out _);
                       lock (_lock)
                           switch (t.Status)
                           {
                               case TaskStatus.Faulted:
                                   foreach (var tcs in _tcs)
                                       tcs.TrySetException(t.Exception.InnerException);
                                   break;
                               case TaskStatus.Canceled:
                                   _tcs.ForEach(tcs => tcs.TrySetCanceled());
                                   break;
                               default:
                                   foreach (var tcs in _tcs)
                                       tcs.TrySetResult(t.Result);
                                   break;
                           };

                   });
            }

            public AwaitedItem Increment(TaskCompletionSource<TResult> tcs)
            {
                lock (_lock)
                    _tcs.Add(tcs);
                return this;
            }

            public void Decrement(TaskCompletionSource<TResult> tcs)
            {
                lock (_lock)
                    _tcs.Remove(tcs);
                if (_tcs.Count == 0)
                    _cts.Cancel();
            }
        }
    }
}