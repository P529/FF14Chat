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
                """;
            command.ExecuteNonQuery();
        }

        Prune();

        writer = Task.Run(WriteLoop);
    }

    public void Enqueue(Message message)
    {
        if (!queue.IsAddingCompleted)
            queue.Add(message);
    }

    /// <summary>Loads the most recent messages in chronological order. Startup only.</summary>
    public List<Message> LoadRecent(int limit)
    {
        var result = new List<Message>();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ts, type, sender, text, tell_partner, sender_raw, message_raw
            FROM messages ORDER BY id DESC LIMIT @limit
            """;
        command.Parameters.AddWithValue("@limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var messageRaw = reader.GetFieldValue<byte[]>(6);
            var parsed = SeString.Parse(messageRaw);

            result.Add(new Message
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)).LocalDateTime,
                Type = (XivChatType)reader.GetInt32(1),
                Sender = reader.GetString(2),
                Text = reader.GetString(3),
                TellPartner = reader.IsDBNull(4) ? null : reader.GetString(4),
                SenderRaw = reader.GetFieldValue<byte[]>(5),
                MessageRaw = messageRaw,
                Segments = MessageParser.Parse(parsed),
            });
        }

        result.Reverse();
        return result;
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
            INSERT INTO messages (ts, type, sender, text, tell_partner, sender_raw, message_raw)
            VALUES (@ts, @type, @sender, @text, @partner, @senderRaw, @messageRaw)
            """;

        var ts = command.Parameters.Add("@ts", SqliteType.Integer);
        var type = command.Parameters.Add("@type", SqliteType.Integer);
        var sender = command.Parameters.Add("@sender", SqliteType.Text);
        var text = command.Parameters.Add("@text", SqliteType.Text);
        var partner = command.Parameters.Add("@partner", SqliteType.Text);
        var senderRaw = command.Parameters.Add("@senderRaw", SqliteType.Blob);
        var messageRaw = command.Parameters.Add("@messageRaw", SqliteType.Blob);

        foreach (var message in batch)
        {
            ts.Value = new DateTimeOffset(message.Timestamp).ToUnixTimeMilliseconds();
            type.Value = (int)message.Type;
            sender.Value = message.Sender;
            text.Value = message.Text;
            partner.Value = (object?)message.TellPartner ?? DBNull.Value;
            senderRaw.Value = message.SenderRaw;
            messageRaw.Value = message.MessageRaw;
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
}
