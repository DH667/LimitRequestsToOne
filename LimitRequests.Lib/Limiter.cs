using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LimitRequests.Lib
{
    public class Limiter<T>
    {
        private readonly ConcurrentDictionary<string, AwaitedItem> actions = new ConcurrentDictionary<string, AwaitedItem>();

        public Task<T> DoLimit(string key, Func<CancellationToken, Task<T>> func, CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return Task.FromCanceled<T>(token);

            var tcs = new TaskCompletionSource<T>();

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

        class AwaitedItem
        {
            private readonly object _lock = new object();
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly Func<CancellationToken, Task<T>> _task;
            private List<TaskCompletionSource<T>> _tcs = new List<TaskCompletionSource<T>>();

            public AwaitedItem(
                Action remove,
                Func<CancellationToken, Task<T>> task,
                TaskCompletionSource<T> tcs)
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

                _task = task;
            }

            public void Increment(TaskCompletionSource<T> tcs)
            {
                lock (_lock)
                {
                    _tcs.Add(tcs);
                }
            }

            public void Decrement(TaskCompletionSource<T> tcs)
            {
                lock (_lock)
                {
                    _tcs.Remove(tcs);
                }
                if (_tcs.Count == 0)
                    _cts.Cancel();
            }
        }
    }
}