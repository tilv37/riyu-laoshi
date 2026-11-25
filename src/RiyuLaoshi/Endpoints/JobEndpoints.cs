using RiyuLaoshi.Data;
using RiyuLaoshi.Services;

namespace RiyuLaoshi.Endpoints;

// 任务中心接口：任务列表、日志，以及失败/完成任务的重新执行
public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        app.MapGet("/api/memo-jobs", (Db db) => db.ListJobs());

        app.MapGet("/api/memo-jobs/{id:long}/logs", (long id, Db db) => db.GetJobLogs(id));

        // 重试会重置任务进度并重新入队，历史日志保留供排查
        app.MapPost("/api/memo-jobs/{id:long}/retry", async (long id, Db db, MemoJobQueue queue) =>
        {
            var job = db.GetJob(id);
            if (job is null)
            {
                return Results.NotFound();
            }

            if (job.Status is "queued" or "running")
            {
                return Results.Conflict();
            }

            db.ResetJobForRetry(id);
            db.UpdateMemoStatus(job.SessionId, "queued");
            await queue.EnqueueAsync(id);
            return Results.Ok(new MemoJobTriggerResponse(id, "queued"));
        });
    }
}
