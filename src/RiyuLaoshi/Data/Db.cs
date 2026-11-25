using System.Globalization;
using Microsoft.Data.Sqlite;
using RiyuLaoshi.Services;

namespace RiyuLaoshi.Data;

public sealed class MemoJobRow
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public string Status { get; set; } = "queued";
    public int TotalChunks { get; set; }
    public int DoneChunks { get; set; }
    public string? Error { get; set; }
    public int RetryCount { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? FinishedAt { get; set; }
}

public sealed class Db
{
    private readonly string _connectionString;

    public Db(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Initialize()
    {
        using var connection = Open();
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Sessions (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                title         TEXT NOT NULL DEFAULT '新会话',
                archived      INTEGER NOT NULL DEFAULT 0,
                memo_status   TEXT NOT NULL DEFAULT 'none',
                created_at    TEXT NOT NULL,
                archived_at   TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Messages (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id    INTEGER NOT NULL REFERENCES Sessions(id) ON DELETE CASCADE,
                role          TEXT NOT NULL,
                content       TEXT NOT NULL,
                created_at    TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_messages_session ON Messages(session_id);

            CREATE TABLE IF NOT EXISTS Memos (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                source_session_id INTEGER NULL REFERENCES Sessions(id) ON DELETE SET NULL,
                title             TEXT NOT NULL,
                category          TEXT NOT NULL,
                tags              TEXT NOT NULL DEFAULT '',
                summary_md        TEXT NOT NULL,
                my_note           TEXT NOT NULL DEFAULT '',
                created_at        TEXT NOT NULL,
                updated_at        TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_memos_category ON Memos(category);
            CREATE INDEX IF NOT EXISTS idx_memos_source ON Memos(source_session_id);

            CREATE TABLE IF NOT EXISTS MemoJobs (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id    INTEGER NOT NULL REFERENCES Sessions(id) ON DELETE CASCADE,
                status        TEXT NOT NULL DEFAULT 'queued',
                total_chunks  INTEGER NOT NULL DEFAULT 0,
                done_chunks   INTEGER NOT NULL DEFAULT 0,
                error         TEXT NULL,
                retry_count   INTEGER NOT NULL DEFAULT 0,
                created_at    TEXT NOT NULL,
                finished_at   TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_memojobs_status ON MemoJobs(status);

            CREATE TABLE IF NOT EXISTS MemoJobLogs (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id        INTEGER NOT NULL REFERENCES MemoJobs(id) ON DELETE CASCADE,
                level         TEXT NOT NULL,
                step          TEXT NOT NULL,
                message       TEXT NOT NULL,
                tokens_used   INTEGER NULL,
                created_at    TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_memojoblogs_job ON MemoJobLogs(job_id);

            CREATE TABLE IF NOT EXISTS Settings (
                key           TEXT PRIMARY KEY,
                value         TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        using var seed = connection.CreateCommand();
        seed.CommandText = "INSERT OR IGNORE INTO Settings(key, value) VALUES (@k, @v);";
        var keyParam = seed.Parameters.Add("@k", SqliteType.Text);
        var valueParam = seed.Parameters.Add("@v", SqliteType.Text);
        foreach (var (key, value) in SettingsDefaults.All)
        {
            keyParam.Value = key;
            valueParam.Value = value;
            seed.ExecuteNonQuery();
        }
    }

    public void ResetInterruptedJobs()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using (var sessions = connection.CreateCommand())
        {
            sessions.Transaction = transaction;
            sessions.CommandText = """
                UPDATE Sessions SET memo_status='failed'
                WHERE memo_status IN ('queued','running')
                  AND id IN (SELECT session_id FROM MemoJobs WHERE status IN ('queued','running'));
                """;
            sessions.ExecuteNonQuery();
        }

        using (var jobs = connection.CreateCommand())
        {
            jobs.Transaction = transaction;
            jobs.CommandText = """
                UPDATE MemoJobs
                SET status='failed', error='服务重启，任务已中断，请到任务中心手动重试', finished_at=@now
                WHERE status IN ('queued','running');
                """;
            jobs.Parameters.AddWithValue("@now", Now());
            jobs.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // ---------- Settings ----------

    public Dictionary<string, string> GetSettings()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM Settings;";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    public void SetSettings(Dictionary<string, string> values)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Settings(key, value) VALUES (@k, @v) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
        var keyParam = command.Parameters.Add("@k", SqliteType.Text);
        var valueParam = command.Parameters.Add("@v", SqliteType.Text);
        foreach (var (key, value) in values)
        {
            keyParam.Value = key;
            valueParam.Value = value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // ---------- Sessions ----------

    public List<SessionDto> GetSessions(bool? archived)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = archived is null
            ? "SELECT id, title, archived, memo_status, created_at, archived_at FROM Sessions ORDER BY archived ASC, id DESC;"
            : "SELECT id, title, archived, memo_status, created_at, archived_at FROM Sessions WHERE archived=@archived ORDER BY id DESC;";
        if (archived is not null)
        {
            command.Parameters.AddWithValue("@archived", archived.Value ? 1 : 0);
        }

        using var reader = command.ExecuteReader();
        var result = new List<SessionDto>();
        while (reader.Read())
        {
            result.Add(ReadSession(reader));
        }

        return result;
    }

    public SessionDto? GetSession(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, archived, memo_status, created_at, archived_at FROM Sessions WHERE id=@id;";
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    private static SessionDto ReadSession(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        Title: reader.GetString(1),
        Archived: reader.GetInt64(2) != 0,
        MemoStatus: reader.GetString(3),
        CreatedAt: reader.GetString(4),
        ArchivedAt: reader.IsDBNull(5) ? null : reader.GetString(5));

    public long CreateSession(string title)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Sessions(title, archived, memo_status, created_at) VALUES (@title, 0, 'none', @now); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@now", Now());
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public bool RenameSession(long id, string title)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Sessions SET title=@title WHERE id=@id;";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@title", title);
        return command.ExecuteNonQuery() > 0;
    }

    public void TrySetAutoTitle(long id, string title)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Sessions SET title=@title
            WHERE id=@id AND (title IS NULL OR title='' OR title='新会话');
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@title", title);
        command.ExecuteNonQuery();
    }

    public bool ArchiveSession(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Sessions SET archived=1, archived_at=@now WHERE id=@id AND archived=0;";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@now", Now());
        return command.ExecuteNonQuery() > 0;
    }

    public void DeleteSession(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Sessions WHERE id=@id;";
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    public (int Rounds, long Chars) GetSessionStats(long sessionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(LENGTH(content)), 0) FROM Messages WHERE session_id=@sid;";
        command.Parameters.AddWithValue("@sid", sessionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return (0, 0);
        }

        var count = (int)reader.GetInt64(0);
        var chars = reader.GetInt64(1);
        return (count / 2, chars);
    }

    public void UpdateMemoStatus(long sessionId, string status)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Sessions SET memo_status=@status WHERE id=@id;";
        command.Parameters.AddWithValue("@id", sessionId);
        command.Parameters.AddWithValue("@status", status);
        command.ExecuteNonQuery();
    }

    // ---------- Messages ----------

    public List<MessageDto> GetMessages(long sessionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, role, content, created_at FROM Messages WHERE session_id=@sid ORDER BY id ASC;";
        command.Parameters.AddWithValue("@sid", sessionId);
        using var reader = command.ExecuteReader();
        var result = new List<MessageDto>();
        while (reader.Read())
        {
            result.Add(new MessageDto(
                Id: reader.GetInt64(0),
                Role: reader.GetString(1),
                Content: reader.GetString(2),
                CreatedAt: reader.GetString(3)));
        }

        return result;
    }

    public (long UserId, long AssistantId) InsertExchange(long sessionId, string question, string answer)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        long Insert(string role, string content)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO Messages(session_id, role, content, created_at) VALUES (@sid, @role, @content, @now); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("@sid", sessionId);
            command.Parameters.AddWithValue("@role", role);
            command.Parameters.AddWithValue("@content", content);
            command.Parameters.AddWithValue("@now", Now());
            return (long)(command.ExecuteScalar() ?? 0L);
        }

        var userId = Insert("user", question);
        var assistantId = Insert("assistant", answer);
        transaction.Commit();
        return (userId, assistantId);
    }

    public void UpdateMessageContent(long messageId, string content)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Messages SET content=@content WHERE id=@id;";
        command.Parameters.AddWithValue("@id", messageId);
        command.Parameters.AddWithValue("@content", content);
        command.ExecuteNonQuery();
    }

    // ---------- Memo jobs ----------

    public long CreateMemoJob(long sessionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO MemoJobs(session_id, status, total_chunks, done_chunks, retry_count, created_at)
            VALUES (@sid, 'queued', 0, 0, 0, @now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@sid", sessionId);
        command.Parameters.AddWithValue("@now", Now());
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public MemoJobRow? GetJob(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, status, total_chunks, done_chunks, error, retry_count, created_at, finished_at
            FROM MemoJobs WHERE id=@id;
            """;
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return ReadJob(reader);
    }

    private static MemoJobRow ReadJob(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        SessionId = reader.GetInt64(1),
        Status = reader.GetString(2),
        TotalChunks = reader.GetInt32(3),
        DoneChunks = reader.GetInt32(4),
        Error = reader.IsDBNull(5) ? null : reader.GetString(5),
        RetryCount = reader.GetInt32(6),
        CreatedAt = reader.GetString(7),
        FinishedAt = reader.IsDBNull(8) ? null : reader.GetString(8)
    };

    public List<MemoJobDto> ListJobs()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT j.id, j.session_id, s.title, j.status, j.total_chunks, j.done_chunks, j.error,
                   j.retry_count, j.created_at, j.finished_at
            FROM MemoJobs j
            JOIN Sessions s ON s.id = j.session_id
            ORDER BY j.id DESC;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<MemoJobDto>();
        while (reader.Read())
        {
            result.Add(new MemoJobDto(
                Id: reader.GetInt64(0),
                SessionId: reader.GetInt64(1),
                SessionTitle: reader.GetString(2),
                Status: reader.GetString(3),
                TotalChunks: reader.GetInt32(4),
                DoneChunks: reader.GetInt32(5),
                Error: reader.IsDBNull(6) ? null : reader.GetString(6),
                RetryCount: reader.GetInt32(7),
                CreatedAt: reader.GetString(8),
                FinishedAt: reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return result;
    }

    public void ResetJobForRetry(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE MemoJobs
            SET status='queued', total_chunks=0, done_chunks=0, error=NULL, finished_at=NULL, retry_count=retry_count+1
            WHERE id=@id;
            """;
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    public void SetJobRunning(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE MemoJobs SET status='running' WHERE id=@id;";
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    public void SetJobTotal(long id, int total)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE MemoJobs SET total_chunks=@total WHERE id=@id;";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@total", total);
        command.ExecuteNonQuery();
    }

    public void SetJobProgress(long id, int done)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE MemoJobs SET done_chunks=@done WHERE id=@id;";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@done", done);
        command.ExecuteNonQuery();
    }

    public void FinishJob(long id, string status)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE MemoJobs SET status=@status, finished_at=@now WHERE id=@id;";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@now", Now());
        command.ExecuteNonQuery();
    }

    public void FailJob(long id, string error)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE MemoJobs SET status='failed', error=@error, finished_at=@now WHERE id=@id;";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@error", Truncate(error, 2000));
        command.Parameters.AddWithValue("@now", Now());
        command.ExecuteNonQuery();
    }

    public void AddJobLog(long jobId, string level, string step, string message, int? tokensUsed = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO MemoJobLogs(job_id, level, step, message, tokens_used, created_at)
            VALUES (@job, @level, @step, @message, @tokens, @now);
            """;
        command.Parameters.AddWithValue("@job", jobId);
        command.Parameters.AddWithValue("@level", level);
        command.Parameters.AddWithValue("@step", step);
        command.Parameters.AddWithValue("@message", Truncate(message, 8000));
        command.Parameters.AddWithValue("@tokens", tokensUsed is null ? DBNull.Value : tokensUsed.Value);
        command.Parameters.AddWithValue("@now", Now());
        command.ExecuteNonQuery();
    }

    public List<MemoJobLogDto> GetJobLogs(long jobId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, level, step, message, tokens_used, created_at
            FROM MemoJobLogs WHERE job_id=@job ORDER BY id ASC;
            """;
        command.Parameters.AddWithValue("@job", jobId);
        using var reader = command.ExecuteReader();
        var result = new List<MemoJobLogDto>();
        while (reader.Read())
        {
            result.Add(new MemoJobLogDto(
                Id: reader.GetInt64(0),
                Level: reader.GetString(1),
                Step: reader.GetString(2),
                Message: reader.GetString(3),
                TokensUsed: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                CreatedAt: reader.GetString(5)));
        }

        return result;
    }

    // ---------- Memos ----------

    public List<MemoDto> ListMemos(string? category, string? tag, string? query)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(category) && MemoCategories.IsValid(category))
        {
            where.Add("category=@category");
            command.Parameters.AddWithValue("@category", category);
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            where.Add("tags LIKE @tag");
            command.Parameters.AddWithValue("@tag", $"%{tag.Trim()}%");
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            where.Add("(title LIKE @q OR summary_md LIKE @q OR my_note LIKE @q OR tags LIKE @q)");
            command.Parameters.AddWithValue("@q", $"%{query.Trim()}%");
        }

        command.CommandText = """
            SELECT id, source_session_id, title, category, tags, summary_md, my_note, created_at, updated_at
            FROM Memos
            """ + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY id DESC;";

        using var reader = command.ExecuteReader();
        var result = new List<MemoDto>();
        while (reader.Read())
        {
            result.Add(ReadMemo(reader));
        }

        return result;
    }

    public MemoDto? GetMemo(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, source_session_id, title, category, tags, summary_md, my_note, created_at, updated_at
            FROM Memos WHERE id=@id;
            """;
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMemo(reader) : null;
    }

    private static MemoDto ReadMemo(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        SourceSessionId: reader.IsDBNull(1) ? null : reader.GetInt64(1),
        Title: reader.GetString(2),
        Category: reader.GetString(3),
        Tags: reader.GetString(4),
        SummaryMd: reader.GetString(5),
        MyNote: reader.GetString(6),
        CreatedAt: reader.GetString(7),
        UpdatedAt: reader.GetString(8));

    public int ReplaceMemosForSession(long sessionId, IReadOnlyList<MemoDraft> drafts)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM Memos WHERE source_session_id=@sid;";
            delete.Parameters.AddWithValue("@sid", sessionId);
            delete.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Memos(source_session_id, title, category, tags, summary_md, my_note, created_at, updated_at)
                VALUES (@sid, @title, @category, @tags, @summary, '', @now, @now);
                """;
            var sid = insert.Parameters.Add("@sid", SqliteType.Integer);
            var title = insert.Parameters.Add("@title", SqliteType.Text);
            var category = insert.Parameters.Add("@category", SqliteType.Text);
            var tags = insert.Parameters.Add("@tags", SqliteType.Text);
            var summary = insert.Parameters.Add("@summary", SqliteType.Text);
            insert.Parameters.AddWithValue("@now", Now());

            foreach (var draft in drafts)
            {
                sid.Value = sessionId;
                title.Value = draft.Title;
                category.Value = draft.Category;
                tags.Value = draft.TagsText;
                summary.Value = draft.SummaryMd;
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return drafts.Count;
    }

    public bool UpdateMemo(long id, string? category, string? tags, string? myNote)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        var sets = new List<string> { "updated_at=@now" };
        if (category is not null)
        {
            sets.Add("category=@category");
            command.Parameters.AddWithValue("@category", category);
        }

        if (tags is not null)
        {
            sets.Add("tags=@tags");
            command.Parameters.AddWithValue("@tags", tags);
        }

        if (myNote is not null)
        {
            sets.Add("my_note=@note");
            command.Parameters.AddWithValue("@note", myNote);
        }

        command.CommandText = $"UPDATE Memos SET {string.Join(", ", sets)} WHERE id=@id;";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@now", Now());
        return command.ExecuteNonQuery() > 0;
    }

    public void DeleteMemo(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Memos WHERE id=@id;";
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    public List<MemoDto> GetMemosForExport()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, source_session_id, title, category, tags, summary_md, my_note, created_at, updated_at
            FROM Memos
            ORDER BY CASE category WHEN '单词' THEN 0 ELSE 1 END, created_at ASC, id ASC;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<MemoDto>();
        while (reader.Read())
        {
            result.Add(ReadMemo(reader));
        }

        return result;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
