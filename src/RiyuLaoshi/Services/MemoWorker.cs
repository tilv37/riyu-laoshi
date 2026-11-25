using System.Threading.Channels;
using RiyuLaoshi.Data;

namespace RiyuLaoshi.Services;

// 整理任务的内存队列：单 worker 顺序消费，FIFO
public sealed class MemoJobQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();

    public ValueTask EnqueueAsync(long jobId, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(jobId, ct);

    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}

// 后台 worker：逐条执行整理任务；失败只挂起当前任务，不阻塞新聊天
public sealed class MemoWorker(
    MemoJobQueue queue,
    Db db,
    MemoPipeline pipeline,
    ILogger<MemoWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            MemoJobRow? job = null;
            try
            {
                job = db.GetJob(jobId);
                if (job is null || job.Status != "queued")
                {
                    continue;
                }

                // 任务状态与会话状态同步推进，前端轮询据此刷新进度
                db.SetJobRunning(jobId);
                db.UpdateMemoStatus(job.SessionId, "running");
                var status = await pipeline.RunAsync(jobId, stoppingToken);
                db.FinishJob(jobId, status);
            }
            // 应用关闭时保留 running 状态，重启由 ResetInterruptedJobs 统一标记失败
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "备忘录整理任务 {JobId} 失败", jobId);
                db.FailJob(jobId, ex.Message);
                if (job is not null)
                {
                    db.UpdateMemoStatus(job.SessionId, "failed");
                }

                try
                {
                    db.AddJobLog(jobId, "error", "failed", ex.ToString());
                }
                catch (Exception logEx)
                {
                    logger.LogError(logEx, "写入任务 {JobId} 错误日志失败", jobId);
                }
            }
        }
    }
}
