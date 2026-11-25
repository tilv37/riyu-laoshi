using RiyuLaoshi.Data;
using RiyuLaoshi.Services;

namespace RiyuLaoshi.Endpoints;

// 会话接口：列表、详情、重命名、归档、删除，以及手动触发整理
public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/sessions", (bool? archived, Db db) => db.GetSessions(archived));

        app.MapPost("/api/sessions", (Db db) =>
        {
            var id = db.CreateSession("新会话");
            return new CreateSessionResponse(id, "新会话");
        });

        app.MapGet("/api/sessions/{id:long}", (long id, Db db) =>
        {
            var session = db.GetSession(id);
            return session is null
                ? Results.NotFound()
                : Results.Ok(new SessionDetailDto(session, db.GetMessages(id)));
        });

        app.MapPatch("/api/sessions/{id:long}", (long id, RenameRequest request, Db db) =>
        {
            var title = (request.Title ?? "").Trim();
            if (title.Length == 0)
            {
                return Results.BadRequest();
            }

            if (title.Length > 100)
            {
                title = title[..100];
            }

            return db.RenameSession(id, title) ? Results.NoContent() : Results.NotFound();
        });

        app.MapPost("/api/sessions/{id:long}/archive", (long id, Db db) =>
        {
            var session = db.GetSession(id);
            if (session is null)
            {
                return Results.NotFound();
            }

            if (!session.Archived)
            {
                db.ArchiveSession(id);
                session = db.GetSession(id);
            }

            return Results.Ok(session);
        });

        app.MapDelete("/api/sessions/{id:long}", (long id, Db db) =>
        {
            db.DeleteSession(id);
            return Results.NoContent();
        });

        // 只允许归档会话触发；进行中的任务直接拒绝，避免重复入队
        app.MapPost("/api/sessions/{id:long}/memo-job", async (long id, Db db, MemoJobQueue queue) =>
        {
            var session = db.GetSession(id);
            if (session is null)
            {
                return Results.NotFound();
            }

            if (!session.Archived)
            {
                return Results.BadRequest();
            }

            if (session.MemoStatus is "queued" or "running")
            {
                return Results.Conflict();
            }

            var jobId = db.CreateMemoJob(id);
            db.UpdateMemoStatus(id, "queued");
            await queue.EnqueueAsync(jobId);
            return Results.Ok(new MemoJobTriggerResponse(jobId, "queued"));
        });
    }
}
