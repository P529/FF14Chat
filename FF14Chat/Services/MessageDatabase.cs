using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FF14Chat.Model;
using Microsoft.Data.Sqlite;

namespace FF14Chat.Services;

/// <summary>
/// SQLite-backed chat history. Writes happen on a single background thread;
/// reads only occur during startup hydration, before the writer has traffic.
/// </summary>
public sealed class MessageDatabase : IDisposable
{
    private const int RetentionDays = 30;
    private const int BatchSize = 100;

    private readonly SqliteConnection connection;
    private readonly BlockingCollection<Message> queue = new(new ConcurrentQueue<Message>());
    private readonly Task writer;

    public MessageDatabase(string path)
    {
        connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS messages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts INTEGER NOT NULL,
                    type INTEGER NOT NULL,
                    sender TEXT NOT NULL,
                    text TEXT NOT NULL,
                    tell_partner TEXT,
                    sender_raw BLOB NOT NULL,
                    message_raw BLOB NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_messages_ts ON messages(ts);
                CREATE INDEX IF NOT EXISTS idx_messages_partner ON messages(tell_partner, ts);
                PRAGMA journal_size_limit=8388608;
                """;
            command.ExecuteNonQuery();
        }

        // Later addition; SQLite has no ADD COLUMN IF NOT EXISTS.
        using (var command = connection.CreateCommand())
        {
            try
            {
                command.CommandText = "ALTER TABLE messages ADD COLUMN sender_job INTEGER";
                command.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Column already exists.
            }
        }

        Prune();
        PurgeBattleSpam();

        writer = Task.Run(WriteLoop);
    }

    public void Enqueue(Message message)
    {
        if (!queue.IsAddingCompleted)
            queue.Add(message);
    }

    /// <summary>
    /// Loads history for startup hydration in chronological order: a recent
    /// window of everything plus a deeper window of tells, so busy system
    /// spam cannot push conversations out of range. Startup only.
    /// </summary>
    public List<Message> LoadForHydration(int recentLimit, int tellLimit)
    {
        var byId = new SortedDictionary<long, Message>();
        Collect(byId, "SELECT id, ts, type, sender, text, tell_partner, sender_raw, message_raw, sender_job FROM messages ORDER BY id DESC LIMIT @limit", recentLimit);
        Collect(byId, "SELECT id, ts, type, sender, text, tell_partner, sender_raw, message_raw, sender_job FROM messages WHERE tell_partner IS NOT NULL ORDER BY id DESC LIMIT @limit", tellLimit);
        return [.. byId.Values];
    }

    private void Collect(SortedDictionary<long, Message> byId, string sql, int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (byId.ContainsKey(id))
                continue;

            var senderRaw = reader.GetFieldValue<byte[]>(6);
            var messageRaw = reader.GetFieldValue<byte[]>(7);
            var parsed = SeString.Parse(messageRaw);

            byId[id] = new Message
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)).LocalDateTime,
                Type = (XivChatType)reader.GetInt32(2),
                Sender = reader.GetString(3),
                Text = reader.GetString(4),
                TellPartner = reader.IsDBNull(5) ? null : reader.GetString(5),
                SenderRaw = senderRaw,
                MessageRaw = messageRaw,
                Segments = MessageParser.Parse(parsed),
                SenderPlayer = MessageParser.ExtractPlayer(SeString.Parse(senderRaw)),
                SenderJob = reader.IsDBNull(8) ? null : (uint)reader.GetInt64(8),
            };
        }
    }

    public void Dispose()
    {
        queue.CompleteAdding();
        try
        {
            writer.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Writer failures are already logged; don't block unload.
        }

        connection.Dispose();
        queue.Dispose();
    }

    private void WriteLoop()
    {
        var batch = new List<Message>(BatchSize);
        while (true)
        {
            Message first;
            try
            {
                if (!queue.TryTake(out first!, Timeout.Infinite))
                    return; // Adding completed and queue drained.
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            batch.Clear();
            batch.Add(first);
            while (batch.Count < BatchSize && queue.TryTake(out var more))
                batch.Add(more);

            try
            {
                WriteBatch(batch);
            }
            catch (Exception e)
            {
                Plugin.Log.Error(e, "Failed to persist {Count} chat messages", batch.Count);
            }
        }
    }

    private void WriteBatch(List<Message> batch)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO messages (ts, type, sender, text, tell_partner, sender_raw, message_raw, sender_job)
            VALUES (@ts, @type, @sender, @text, @partner, @senderRaw, @messageRaw, @senderJob)
            """;

        var ts = command.Parameters.Add("@ts", SqliteType.Integer);
        var type = command.Parameters.Add("@type", SqliteType.Integer);
        var sender = command.Parameters.Add("@sender", SqliteType.Text);
        var text = command.Parameters.Add("@text", SqliteType.Text);
        var partner = command.Parameters.Add("@partner", SqliteType.Text);
        var senderRaw = command.Parameters.Add("@senderRaw", SqliteType.Blob);
        var messageRaw = command.Parameters.Add("@messageRaw", SqliteType.Blob);
        var senderJob = command.Parameters.Add("@senderJob", SqliteType.Integer);

        foreach (var message in batch)
        {
            ts.Value = new DateTimeOffset(message.Timestamp).ToUnixTimeMilliseconds();
            type.Value = (int)message.Type;
            sender.Value = message.Sender;
            text.Value = message.Text;
            partner.Value = (object?)message.TellPartner ?? DBNull.Value;
            senderRaw.Value = message.SenderRaw;
            messageRaw.Value = message.MessageRaw;
            senderJob.Value = (object?)message.SenderJob ?? DBNull.Value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void Prune()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM messages WHERE ts < @cutoff";
        command.Parameters.AddWithValue(
            "@cutoff", DateTimeOffset.UtcNow.AddDays(-RetentionDays).ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Battle log rows are no longer captured; clear out what older versions
    /// persisted (they were ~85% of the file) and reclaim the space once.
    /// </summary>
    private void PurgeBattleSpam()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM messages WHERE (type & 0x7F) BETWEEN 41 AND 55";
        var removed = command.ExecuteNonQuery();

        if (removed > 0)
        {
            Plugin.Log.Information("Purged {Count} battle log rows from chat history", removed);
            using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM";
            vacuum.ExecuteNonQuery();
        }
    }
}
