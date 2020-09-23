using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace LimitRequests.Lib
{
    public class FutureLimiter<TKey, TResult>
    {
        private readonly object _lock = new object();
        private readonly ConcurrentDictionary<TKey, AwaitedItem> _futures = new ConcurrentDictionary<TKey, AwaitedItem>();

        public Task<TResult> Add(TKey key, Func<CancellationToken, Task<TResult>> func, CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return Task.FromCanceled<TResult>(token);

            var tcs = new TaskCompletionSource<TResult>();

            lock (_lock)
                _futures.AddOrUpdate(
                   key,
                   key => new AwaitedItem(key, _futures, func, tcs, token),
                   (key, current) => current.Increment(tcs, token));

            return tcs.Task;
        }

        public bool TryInvalidate(TKey key)
        {
            lock (_lock) return _futures.TryRemove(key, out _);
        }

        public void InvalidateAll() => _futures.Clear();

        class AwaitedItem
        {
            private readonly object _lock = new object();
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly ConcurrentDictionary<TaskCompletionSource<TResult>, CancellationTokenRegistration> _cancellations =
                new ConcurrentDictionary<TaskCompletionSource<TResult>, CancellationTokenRegistration>();

            public AwaitedItem(
                TKey key,
                ConcurrentDictionary<TKey, AwaitedItem> actions,
                Func<CancellationToken, Task<TResult>> task,
                TaskCompletionSource<TResult> tcs,
                CancellationToken token)
            {
                _cancellations.TryAdd(tcs, token.Register(() =>
                 {
                     Decrement(tcs);
                     tcs.SetCanceled();
                 }));

                task(_cts.Token).ContinueWith(t =>
                   {
                       actions.TryRemove(key, out _);
                       lock (_lock)
                           switch (t.Status)
                           {
                               case TaskStatus.Faulted:
                                   foreach (var (k, v) in _cancellations)
                                   {
                                       k.TrySetException(t.Exception.InnerException);
                                       v.DisposeAsync();
                                   }
                                   break;
                               case TaskStatus.Canceled:
                                   foreach (var (k, v) in _cancellations)
                                   {
                                       k.TrySetCanceled();
                                       v.DisposeAsync();
                                   }
                                   break;
                               default:
                                   foreach (var (k, v) in _cancellations)
                                   {
                                       k.TrySetResult(t.Result);
                                       v.DisposeAsync();
                                   }
                                   break;
                           };
                       _cts.Dispose();
                   });
            }

            public AwaitedItem Increment(TaskCompletionSource<TResult> tcs, CancellationToken token)
            {
                _cancellations.TryAdd(tcs, token.Register(() =>
                 {
                     Decrement(tcs);
                     tcs.SetCanceled();
                 }));

                return this;
            }

            public void Decrement(TaskCompletionSource<TResult> tcs)
            {
                lock (_lock)
                {
                    if (_cancellations.TryRemove(tcs, out var registration))
                        registration.DisposeAsync();
                    if (_cancellations.Count == 0)
                        _cts.Cancel();
                }
            }
        }
    }
}