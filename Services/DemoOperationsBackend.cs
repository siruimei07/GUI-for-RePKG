using FieldStation.Contracts;

namespace FieldStation.Services;

/// <summary>
/// Deterministic, memory-only demonstration backend. It emits real progress states but never
/// reads, writes, or deletes user files.
/// </summary>
public sealed class DemoOperationsBackend : IOperationsBackend
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cycleCancellation;
    private OperationsSnapshot _snapshot = CreateSnapshot();

    public string ProviderName => "MEMORY BRIDGE / LOCAL";

    public event EventHandler<OperationsSnapshot>? SnapshotChanged;

    public Task<OperationsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_snapshot);

    public async Task<OperationResult> StartCycleAsync(string planId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_cycleCancellation is not null)
            {
                return new OperationResult(false, "已有运行中的流程");
            }

            _cycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        try
        {
            for (var step = 0; step <= 100; step += 4)
            {
                _cycleCancellation.Token.ThrowIfCancellationRequested();
                var progress = step / 100d;
                var first = _snapshot.WorkUnits[0] with
                {
                    Stage = progress < 0.85 ? "ASSEMBLY" : "VERIFY",
                    Status = progress < 1 ? "RUNNING" : "COMPLETE",
                    Progress = step
                };
                _snapshot = _snapshot with
                {
                    Mode = progress < 1 ? $"EXECUTING / {planId}" : "READY",
                    OverallProgress = progress,
                    ActiveUnits = progress < 1 ? 3 : 0,
                    ReadyUnits = progress < 1 ? 18 : 21,
                    WorkUnits = new[] { first }.Concat(_snapshot.WorkUnits.Skip(1)).ToArray()
                };
                SnapshotChanged?.Invoke(this, _snapshot);
                await Task.Delay(72, _cycleCancellation.Token);
            }

            return new OperationResult(true, "流程完成：演示状态已验证");
        }
        catch (OperationCanceledException)
        {
            _snapshot = _snapshot with { Mode = "PAUSED", ActiveUnits = 0 };
            SnapshotChanged?.Invoke(this, _snapshot);
            return new OperationResult(true, "流程已停止");
        }
        finally
        {
            lock (_gate)
            {
                _cycleCancellation?.Dispose();
                _cycleCancellation = null;
            }
        }
    }

    public Task<OperationResult> StopCycleAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _cycleCancellation?.Cancel();
        }

        return Task.FromResult(new OperationResult(true, "已发送停止请求"));
    }

    /// <summary>Deterministic in-memory state used only by the visual QA command line.</summary>
    public void SetQaRunningState(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        var first = _snapshot.WorkUnits[0] with
        {
            Stage = "ASSEMBLY",
            Status = "RUNNING",
            Progress = progress * 100
        };
        _snapshot = _snapshot with
        {
            Mode = "EXECUTING / QA",
            OverallProgress = progress,
            ActiveUnits = 3,
            WorkUnits = new[] { first }.Concat(_snapshot.WorkUnits.Skip(1)).ToArray()
        };
    }

    private static OperationsSnapshot CreateSnapshot()
    {
        var workUnits = new[]
        {
            new WorkUnit("WU-01", "核心装配", "ASSEMBLY", "RUNNING", 64, "LOCAL"),
            new WorkUnit("WU-02", "索引同步", "TRANSFER", "QUEUED", 18, "CACHE"),
            new WorkUnit("WU-03", "边界复核", "VERIFY", "ATTENTION", 0, "USER")
        };
        var routes = new[]
        {
            new RouteNode("N-01", "主控节点", "CORE", "ONLINE", 120, 82, "所有任务的调度入口。"),
            new RouteNode("N-02", "北向中继", "RELAY", "ONLINE", 80, 46, "负责工作区索引同步。"),
            new RouteNode("N-03", "资产仓", "STORAGE", "ONLINE", 240, 168, "保存已确认的本地资产。"),
            new RouteNode("N-04", "验证区", "CHECK", "ATTENTION", 60, 54, "两项记录需要人工复核。"),
            new RouteNode("N-05", "输出端", "OUTPUT", "IDLE", 100, 21, "等待新的发布流程。")
        };
        var assets = new[]
        {
            new AssetRecord("AR-2401", "结构蓝图", "DOCUMENT", "VERIFIED", 12, "14:32"),
            new AssetRecord("AR-2402", "界面组件集", "MODULE", "ACTIVE", 8, "14:18"),
            new AssetRecord("AR-2403", "本地索引", "DATA", "SYNCING", 31, "13:56"),
            new AssetRecord("AR-2404", "构建报告", "REPORT", "READY", 6, "12:40"),
            new AssetRecord("AR-2405", "路由快照", "DATA", "ATTENTION", 17, "11:22"),
            new AssetRecord("AR-2406", "扩展清单", "MODULE", "READY", 4, "10:08")
        };
        var reports = new[]
        {
            new ReportPoint("MON", 62, 70), new ReportPoint("TUE", 78, 70),
            new ReportPoint("WED", 74, 72), new ReportPoint("THU", 88, 76),
            new ReportPoint("FRI", 83, 78), new ReportPoint("SAT", 91, 82),
            new ReportPoint("SUN", 86, 84)
        };
        return new OperationsSnapshot(
            "TALOS / A-07", "ACTIVE BUILD", true, 0.64, 18, 3, 2, 86.4,
            workUnits, routes, assets, reports);
    }
}
