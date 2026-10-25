namespace DianXu.Core;

public interface IClickOutput { void Click(ClickPoint point); }
public interface IClickDelay { Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken); }
public sealed class SystemClickDelay : IClickDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
public sealed record ClickProgress(long Count, int PointIndex);
public sealed record ClickRunResult(long Count, bool Completed);

public sealed class ClickEngine(IClickOutput output, IClickDelay? delay = null)
{
    private readonly object _stateGate = new();
    private readonly IClickDelay _delay = delay ?? new SystemClickDelay();
    private CancellationTokenSource? _source;
    public bool IsRunning { get { lock (_stateGate) return _source is not null; } }

    public Task<ClickRunResult> RunAsync(ClickPlan plan, IProgress<ClickProgress>? progress = null)
    {
        // Copy before starting: subsequent UI edits must not alter a running sequence.
        var snapshot = new ClickPlan(plan.Points.ToArray(), plan.Limit);
        snapshot.Validate();
        lock (_stateGate)
        {
            if (_source is not null) throw new InvalidOperationException("已有点击任务在运行，请先停止。");
            var source = new CancellationTokenSource();
            _source = source;
            return Task.Run(async () =>
            {
                long count = 0;
                var token = source.Token;
                try
                {
                    int index = 0;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        output.Click(snapshot.Points[index]);
                        count++;
                        progress?.Report(new ClickProgress(count, index));
                        if (snapshot.Limit is long limit && count >= limit)
                            return new ClickRunResult(count, true);
                        await _delay.WaitAsync(snapshot.Points[index].Delay, token).ConfigureAwait(false);
                        index = (index + 1) % snapshot.Points.Count;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return new ClickRunResult(count, false);
                }
                finally
                {
                    lock (_stateGate) _source = null;
                    source.Dispose();
                }
            });
        }
    }

    public void Stop()
    {
        // Never hold a lock over SendInput: its hook callback runs on the UI thread.
        lock (_stateGate) _source?.Cancel();
    }
}
