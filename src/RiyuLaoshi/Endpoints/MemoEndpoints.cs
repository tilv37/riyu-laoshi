using System.Text;
using RiyuLaoshi.Data;
using RiyuLaoshi.Services;

namespace RiyuLaoshi.Endpoints;

// 错题本接口：筛选列表、人工编辑、删除，以及导出 Markdown 书
public static class MemoEndpoints
{
    public static void MapMemoEndpoints(this WebApplication app)
    {
        app.MapGet("/api/memos", (string? category, string? tag, string? q, Db db) =>
            db.ListMemos(category, tag, q));

        app.MapPatch("/api/memos/{id:long}", (long id, MemoUpdateRequest request, Db db) =>
        {
            string? category = null;
            if (request.Category is not null)
            {
                category = MemoCategories.Normalize(request.Category);
                if (category is null)
                {
                    return Results.BadRequest();
                }
            }

            var tags = request.Tags?.Trim();
            var note = request.MyNote;
            if (!db.UpdateMemo(id, category, tags, note))
            {
                return Results.NotFound();
            }

            var memo = db.GetMemo(id);
            return memo is null ? Results.NotFound() : Results.Ok(memo);
        });

        app.MapDelete("/api/memos/{id:long}", (long id, Db db) =>
        {
            db.DeleteMemo(id);
            return Results.NoContent();
        });

        // 导出按「单词 → 语法」分章，包含标题、整理正文、标签和错题备注
        app.MapGet("/api/memos/export.md", (Db db) =>
        {
            var memos = db.GetMemosForExport();
            var builder = new StringBuilder();
            builder.AppendLine("# 我的日语错题本");
            builder.AppendLine();

            if (memos.Count == 0)
            {
                builder.AppendLine("_暂无错题记录。_");
            }
            else
            {
                foreach (var category in new[] { MemoCategories.Word, MemoCategories.Grammar })
                {
                    var items = memos.Where(m => m.Category == category).ToList();
                    if (items.Count == 0)
                    {
                        continue;
                    }

                    builder.AppendLine($"## {category}");
                    builder.AppendLine();
                    foreach (var memo in items)
                    {
                        builder.AppendLine($"### {memo.Title}");
                        builder.AppendLine();
                        builder.AppendLine(memo.SummaryMd.Trim());
                        builder.AppendLine();
                        if (!string.IsNullOrWhiteSpace(memo.Tags))
                        {
                            builder.AppendLine($"**标签：** {memo.Tags}");
                            builder.AppendLine();
                        }

                        if (!string.IsNullOrWhiteSpace(memo.MyNote))
                        {
                            var note = memo.MyNote.Trim().Replace("\r\n", "\n").Replace("\n", "\n> ");
                            builder.AppendLine($"> 我的错题备注：{note}");
                            builder.AppendLine();
                        }

                        builder.AppendLine("---");
                        builder.AppendLine();
                    }
                }
            }

            var preamble = Encoding.UTF8.GetPreamble();
            var body = Encoding.UTF8.GetBytes(builder.ToString());
            var bytes = new byte[preamble.Length + body.Length];
            preamble.CopyTo(bytes, 0);
            body.CopyTo(bytes, preamble.Length);
            return Results.File(bytes, "text/markdown; charset=utf-8", "japanese-mistake-book.md");
        });
    }
}
