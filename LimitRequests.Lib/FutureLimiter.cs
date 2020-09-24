using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
            AwaitedItem awaitedItem;

            lock (_lock)
                awaitedItem = _futures.AddOrUpdate(
                   key,
                   key => new AwaitedItem(key, _futures, func, tcs),
                   (key, current) => current.Increment(key, _futures, func, tcs));

            var registration = token.Register(() =>
                 {
                     awaitedItem.Decrement(tcs);
                     tcs.SetCanceled();
                 });
            tcs.Task.ContinueWith(t => registration.DisposeAsync());

            return tcs.Task.ContinueWith<TResult>(t =>
            {
                registration.DisposeAsync();
                return t.Status switch
                {
                    TaskStatus.Canceled => throw new TaskCanceledException(),
                    TaskStatus.Faulted => throw t.Exception.InnerException,
                    _ => t.Result
                };
            });
        }

        public bool TryInvalidate(TKey key)
        {
            lock (_lock) return _futures.TryRemove(key, out _);
        }

        public void InvalidateAll() => _futures.Clear();

        class AwaitedItem
        {
            private readonly object _lock = new object();

            private int _awaitersCount = 1;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();

            private readonly List<TaskCompletionSource<TResult>> _taskCompletions = new List<TaskCompletionSource<TResult>>();

            public AwaitedItem(
                TKey key,
                ConcurrentDictionary<TKey, AwaitedItem> futures,
                Func<CancellationToken, Task<TResult>> task,
                TaskCompletionSource<TResult> tcs
                )
            {
                _taskCompletions.Add(tcs);

                task(_cts.Token).ContinueWith(t =>
                   {
                       Interlocked.Exchange(ref _awaitersCount, 0);
                       if (futures[key].Equals(this))
                           futures.TryRemove(key, out _);

                       lock (_lock)
                           switch (t.Status)
                           {
                               case TaskStatus.Faulted:
                                   foreach (var taskCompletion in _taskCompletions)
                                       taskCompletion.TrySetException(t.Exception.InnerException);
                                   break;
                               case TaskStatus.Canceled:
                                   foreach (var taskCompletion in _taskCompletions)
                                       taskCompletion.TrySetCanceled();
                                   break;
                               default:
                                   foreach (var taskCompletion in _taskCompletions)
                                       taskCompletion.TrySetResult(t.Result);
                                   break;
                           };
                       _cts.Dispose();
                   });
            }

            public AwaitedItem Increment(
                TKey key,
                ConcurrentDictionary<TKey, AwaitedItem> futures,
                Func<CancellationToken, Task<TResult>> task,
                TaskCompletionSource<TResult> tcs)
            {
                if (Interlocked.Increment(ref _awaitersCount) == 1)
                    return new AwaitedItem(key, futures, task, tcs);

                lock (_lock)
                    _taskCompletions.Add(tcs);
                return this;
            }

            public void Decrement(TaskCompletionSource<TResult> tcs)
            {
                var awaitersCount = Interlocked.Decrement(ref _awaitersCount);

                if (awaitersCount <= 0)
                    _cts.Cancel();

                lock (_lock)
                    _taskCompletions.Remove(tcs);
            }
        }
    }
}