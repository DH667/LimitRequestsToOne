using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace LimitRequests.Lib
{
    public class FutureLimiter<TKey, TResult>
    {
        private readonly object _lock = new object();
        private readonly ConcurrentDictionary<TKey, AwaitedItem> _futures = new ConcurrentDictionary<TKey, AwaitedItem>();
        public async Task<TResult> Add(TKey key, Func<CancellationToken, Task<TResult>> func, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            AwaitedItem awaitedItem;

            lock (_lock)
                awaitedItem = _futures.AddOrUpdate(
                   key,
                   key => new AwaitedItem(key, _futures, func),
                   (key, current) => current.Increment(key, _futures, func));

            return await await Task.WhenAny(
                OnCancelled(),
                awaitedItem.Task);

            async Task<TResult> OnCancelled()
            {
                try
                {
                    await token;
                }
                catch
                {
                    awaitedItem.Decrement();
                    throw;
                }
                return default;
            }
        }

        public bool TryInvalidate(TKey key)
        {
            lock (_lock) return _futures.TryRemove(key, out _);
        }

        public void InvalidateAll() => _futures.Clear();

        class AwaitedItem
        {
            private int _awaitersCount = 1;
            private CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly TaskCompletionSource<TResult> _tcs = new TaskCompletionSource<TResult>();

            public AwaitedItem(
                TKey key,
                ConcurrentDictionary<TKey, AwaitedItem> futures,
                Func<CancellationToken, Task<TResult>> task)
            => task(_cts.Token).ContinueWith(t =>
                {
                    Interlocked.Exchange(ref _awaitersCount, 0);

                    ((ICollection<KeyValuePair<TKey, AwaitedItem>>)futures)
                        .Remove(new KeyValuePair<TKey, AwaitedItem>(key, this));

                    switch (t.Status)
                    {
                        case TaskStatus.Faulted:
                            _tcs.TrySetException(t.Exception.InnerException);
                            break;
                        case TaskStatus.Canceled:
                            _tcs.TrySetCanceled();
                            break;
                        default:
                            _tcs.TrySetResult(t.Result);
                            break;
                    };
                    _cts.Dispose();
                    _cts = null;
                });

            public Task<TResult> Task => _tcs.Task;

            public AwaitedItem Increment(
                TKey key,
                ConcurrentDictionary<TKey, AwaitedItem> futures,
                Func<CancellationToken, Task<TResult>> task)
            => Interlocked.Increment(ref _awaitersCount) == 1
                    ? new AwaitedItem(key, futures, task)
                    : this;

            public void Decrement()
            {
                if (Interlocked.Decrement(ref _awaitersCount) == 0)
                    _cts?.Cancel();
            }
        }
    }

    public static class AsyncExt
    {
        public static CancellationTokenAwaiter GetAwaiter(this CancellationToken ct)
        => new CancellationTokenAwaiter(ct);
    }

    public struct CancellationTokenAwaiter : INotifyCompletion, ICriticalNotifyCompletion
    {
        private readonly CancellationToken _token;

        public CancellationTokenAwaiter(CancellationToken token)
        => _token = token;

        public object GetResult()
        {
            if (IsCompleted)
                throw new TaskCanceledException();
            throw new InvalidOperationException("Should never see this error");
        }

        public bool IsCompleted => _token.IsCancellationRequested;

        public void OnCompleted(Action continuation)
        => _token.Register(continuation);

        public void UnsafeOnCompleted(Action continuation)
        => _token.Register(continuation);
    }
}