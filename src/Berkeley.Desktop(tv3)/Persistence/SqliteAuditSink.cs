using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using Berkeley.Contracts;
using Microsoft.Data.Sqlite;

namespace Berkeley.Desktop.Persistence;

/// <summary>
/// Triển khai IAuditSink ghi dữ liệu bất đồng bộ vào cơ sở dữ liệu SQLite cục bộ (berkeley_audit.db).
/// Dùng System.Threading.Channels để xếp hàng ghi nền, đảm bảo I/O mạng và giao diện không bị chặn.
/// </summary>
public sealed class SqliteAuditSink : IAuditSink, IAsyncDisposable, IDisposable
{
    private readonly string _connectionString;
    private readonly Channel<AuditEvent> _eventChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;
    private bool _disposed;

    public event Action<AuditEvent>? AuditEventLogged;

    public SqliteAuditSink(string? dbPath = null)
    {
        dbPath ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "berkeley_audit.db");
        _connectionString = $"Data Source={dbPath}";

        _eventChannel = Channel.CreateUnbounded<AuditEvent>(new UnboundedChannelOptions
        {
            SingleReader = true
        });

        InitializeDatabase();
        _writerTask = Task.Run(ProcessQueueAsync);
    }

    private void InitializeDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS audit_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sequence INTEGER,
                occurred_at TEXT,
                monotonic_ms INTEGER,
                source_node_id TEXT,
                kind TEXT,
                summary TEXT,
                detail TEXT
            );

            CREATE TABLE IF NOT EXISTS round_summaries (
                round_id TEXT PRIMARY KEY,
                master_node_id TEXT,
                leader_session_id INTEGER,
                started_at TEXT,
                ended_at TEXT,
                average_offset_ms REAL,
                remeasured_spread_ms REAL,
                outcome TEXT,
                accepted_count INTEGER,
                rejected_count INTEGER,
                raw_json TEXT
            );
        ";
        cmd.ExecuteNonQuery();
    }

    public void Record(AuditEvent auditEvent)
    {
        if (_disposed) return;
        _eventChannel.Writer.TryWrite(auditEvent);
        AuditEventLogged?.Invoke(auditEvent);
    }

    public void RecordRoundSummary(RoundSummary summary)
    {
        Task.Run(async () =>
        {
            try
            {
                await using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO round_summaries (
                        round_id, master_node_id, leader_session_id, started_at, ended_at,
                        average_offset_ms, remeasured_spread_ms, outcome, accepted_count, rejected_count, raw_json
                    ) VALUES (
                        $round_id, $master, $session, $started, $ended,
                        $avg_offset, $spread, $outcome, $acc_count, $rej_count, $raw
                    );";

                cmd.Parameters.AddWithValue("$round_id", summary.RoundId);
                cmd.Parameters.AddWithValue("$master", summary.MasterNodeId);
                cmd.Parameters.AddWithValue("$session", summary.LeaderSessionId);
                cmd.Parameters.AddWithValue("$started", summary.RoundStartedAt.ToString("o"));
                cmd.Parameters.AddWithValue("$ended", summary.RoundEndedAt.ToString("o"));
                cmd.Parameters.AddWithValue("$avg_offset", summary.AverageOffsetMs);
                cmd.Parameters.AddWithValue("$spread", (object?)summary.RemeasuredSpreadMs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$outcome", summary.Outcome.ToString());
                cmd.Parameters.AddWithValue("$acc_count", summary.AcceptedSamples.Count);
                cmd.Parameters.AddWithValue("$rej_count", summary.RejectedSamples.Count);
                cmd.Parameters.AddWithValue("$raw", JsonSerializer.Serialize(summary));

                await cmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // Silent fail to not crash app
            }
        });
    }

    private async Task ProcessQueueAsync()
    {
        var buffer = new List<AuditEvent>(50);

        try
        {
            while (await _eventChannel.Reader.WaitToReadAsync(_cts.Token))
            {
                buffer.Clear();
                while (buffer.Count < 50 && _eventChannel.Reader.TryRead(out var evt))
                {
                    buffer.Add(evt);
                }

                if (buffer.Count > 0)
                {
                    await WriteBatchAsync(buffer);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // draining remaining
            buffer.Clear();
            while (_eventChannel.Reader.TryRead(out var evt))
            {
                buffer.Add(evt);
            }
            if (buffer.Count > 0)
            {
                await WriteBatchAsync(buffer);
            }
        }
    }

    private async Task WriteBatchAsync(List<AuditEvent> batch)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var trans = conn.BeginTransaction();

            using var cmd = conn.CreateCommand();
            cmd.Transaction = trans;
            cmd.CommandText = @"
                INSERT INTO audit_events (
                    sequence, occurred_at, monotonic_ms, source_node_id, kind, summary, detail
                ) VALUES (
                    $seq, $occ, $mono, $src, $kind, $sum, $det
                );";

            var pSeq = cmd.Parameters.Add("$seq", SqliteType.Integer);
            var pOcc = cmd.Parameters.Add("$occ", SqliteType.Text);
            var pMono = cmd.Parameters.Add("$mono", SqliteType.Integer);
            var pSrc = cmd.Parameters.Add("$src", SqliteType.Text);
            var pKind = cmd.Parameters.Add("$kind", SqliteType.Text);
            var pSum = cmd.Parameters.Add("$sum", SqliteType.Text);
            var pDet = cmd.Parameters.Add("$det", SqliteType.Text);

            foreach (var evt in batch)
            {
                pSeq.Value = evt.Sequence;
                pOcc.Value = evt.OccurredAt.ToString("o");
                pMono.Value = evt.MonotonicMs;
                pSrc.Value = evt.SourceNodeId;
                pKind.Value = evt.Kind.ToString();
                pSum.Value = evt.Summary;
                pDet.Value = evt.Detail != null ? JsonSerializer.Serialize(evt.Detail) : (object)DBNull.Value;

                await cmd.ExecuteNonQueryAsync();
            }

            trans.Commit();
        }
        catch
        {
            // Handled
        }
    }

    public async Task<List<AuditEvent>> GetRecentEventsAsync(int limit = 200)
    {
        var list = new List<AuditEvent>();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT sequence, occurred_at, monotonic_ms, source_node_id, kind, summary
                FROM audit_events
                ORDER BY id DESC
                LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                long seq = reader.GetInt64(0);
                var occ = DateTimeOffset.Parse(reader.GetString(1));
                long mono = reader.GetInt64(2);
                string src = reader.GetString(3);
                Enum.TryParse<AuditEventKind>(reader.GetString(4), out var kind);
                string summary = reader.GetString(5);

                list.Add(new AuditEvent(seq, occ, mono, src, kind, summary));
            }
        }
        catch
        {
            // Ignored
        }

        return list;
    }

    public async Task<List<RoundSummaryRecord>> GetRecentRoundsAsync(int limit = 50)
    {
        var list = new List<RoundSummaryRecord>();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT round_id, master_node_id, leader_session_id, started_at, ended_at,
                       average_offset_ms, remeasured_spread_ms, outcome, accepted_count, rejected_count
                FROM round_summaries
                ORDER BY rowid DESC
                LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new RoundSummaryRecord(
                    RoundId: reader.GetString(0),
                    MasterNodeId: reader.GetString(1),
                    LeaderSessionId: reader.GetInt64(2),
                    StartedAt: DateTimeOffset.Parse(reader.GetString(3)),
                    EndedAt: DateTimeOffset.Parse(reader.GetString(4)),
                    AverageOffsetMs: reader.GetDouble(5),
                    RemeasuredSpreadMs: reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    Outcome: reader.GetString(7),
                    AcceptedCount: reader.GetInt32(8),
                    RejectedCount: reader.GetInt32(9)
                ));
            }
        }
        catch
        {
            // Ignored
        }

        return list;
    }

    public async Task ExportAuditToCsvAsync(string filePath)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sequence, occurred_at, source_node_id, kind, summary FROM audit_events ORDER BY id ASC";

        await using var writer = new StreamWriter(filePath);
        await writer.WriteLineAsync("Sequence,OccurredAt,SourceNodeId,Kind,Summary");

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string line = $"{reader.GetInt64(0)},\"{reader.GetString(1)}\",\"{reader.GetString(2)}\",\"{reader.GetString(3)}\",\"{reader.GetString(4).Replace("\"", "\"\"")}\"";
            await writer.WriteLineAsync(line);
        }
    }

    public async Task ClearAllAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM audit_events; DELETE FROM round_summaries; VACUUM;";
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _eventChannel.Writer.Complete();
        _writerTask.Wait(1000);
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _eventChannel.Writer.Complete();
        try
        {
            await _writerTask;
        }
        catch
        {
            // Ignore
        }
        _cts.Dispose();
    }
}

public record RoundSummaryRecord(
    string RoundId,
    string MasterNodeId,
    long LeaderSessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    double AverageOffsetMs,
    double? RemeasuredSpreadMs,
    string Outcome,
    int AcceptedCount,
    int RejectedCount
);
