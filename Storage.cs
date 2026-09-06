using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ForumReviewMonitor;

public static class Storage
{
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static string? LoadWarning { get; private set; }
    public static Settings LoadSettings()
    {
        Directory.CreateDirectory(DirectoryPath);
        string path = Path.Combine(DirectoryPath, "settings.dat");
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<Settings>(Dpapi.Transform(File.ReadAllBytes(path), false), Options) ?? throw new InvalidDataException(); }
        catch { LoadWarning = "配置无法解密或已损坏，已加载默认值。原配置尚未覆盖；跨电脑或 Windows 账号迁移后需重新填写凭据。"; return new(); }
    }
    public static void SaveSettings(Settings settings) => AtomicWrite(Path.Combine(DirectoryPath, "settings.dat"), Dpapi.Transform(JsonSerializer.SerializeToUtf8Bytes(settings, Options), true));
    private static string ForumFileKey(Settings settings) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.BaseUri.AbsoluteUri)))[..16];
    private static string StatePath(Settings settings) => Path.Combine(DirectoryPath, $"state-{ForumFileKey(settings)}.json");
    private static string StatsPath(Settings settings) => Path.Combine(DirectoryPath, $"stats-{ForumFileKey(settings)}.json");
    private static string DatabasePath(Settings settings) => Path.Combine(DirectoryPath, $"monitor-{ForumFileKey(settings)}.db");
    private static SqliteConnection OpenDatabase(Settings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var connection = new SqliteConnection($"Data Source={DatabasePath(settings)};Mode=ReadWriteCreate");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS delivered (channel TEXT NOT NULL, reviewable_id INTEGER NOT NULL, PRIMARY KEY (channel, reviewable_id));
            CREATE TABLE IF NOT EXISTS reminders (channel TEXT PRIMARY KEY, sent_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS review_stats (
              id INTEGER PRIMARY KEY, type TEXT NOT NULL, title TEXT NOT NULL, author TEXT NOT NULL,
              post_created_at TEXT NULL, review_queued_at TEXT NULL, first_seen TEXT NOT NULL,
              completed_observed_at TEXT NULL, server_completed_at TEXT NULL);
            """;
        command.ExecuteNonQuery();
        return connection;
    }
    private static string? Text(DateTimeOffset? value) => value?.ToString("O");
    private static DateTimeOffset? Time(string? value) => DateTimeOffset.TryParse(value, out var time) ? time : null;
    private static void MigrateLegacy(Settings settings, SqliteConnection connection)
    {
        using var count = connection.CreateCommand(); count.CommandText = "SELECT (SELECT COUNT(*) FROM delivered) + (SELECT COUNT(*) FROM review_stats);";
        if (Convert.ToInt64(count.ExecuteScalar()) != 0) return;
        bool migrated = false;
        string statePath = StatePath(settings), statsPath = StatsPath(settings);
        try
        {
            if (File.Exists(statePath)) { SaveState(connection, JsonSerializer.Deserialize<DeliveryState>(File.ReadAllText(statePath), Options) ?? new()); migrated = true; }
            if (File.Exists(statsPath)) { SaveStats(connection, JsonSerializer.Deserialize<ReviewStats>(File.ReadAllText(statsPath), Options) ?? new()); migrated = true; }
            if (migrated)
            {
                if (File.Exists(statePath)) File.Move(statePath, statePath + ".migrated", true);
                if (File.Exists(statsPath)) File.Move(statsPath, statsPath + ".migrated", true);
            }
        }
        catch (Exception ex) { throw new InvalidOperationException("本地 JSON 数据迁移到 SQLite 失败；原文件未删除。", ex); }
    }
    public static ReviewStats LoadStats(Settings settings)
    {
        using var connection = OpenDatabase(settings); MigrateLegacy(settings, connection);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT id, type, title, author, post_created_at, review_queued_at, first_seen, completed_observed_at, server_completed_at FROM review_stats";
        using var reader = command.ExecuteReader(); var stats = new ReviewStats();
        while (reader.Read()) stats.Records.Add(new ReviewStatsRecord { Id = reader.GetInt64(0), Type = reader.GetString(1), Title = reader.GetString(2), Author = reader.GetString(3), PostCreatedAt = Time(reader.IsDBNull(4) ? null : reader.GetString(4)), ReviewQueuedAt = Time(reader.IsDBNull(5) ? null : reader.GetString(5)), FirstSeen = Time(reader.GetString(6)) ?? DateTimeOffset.MinValue, CompletedAt = Time(reader.IsDBNull(7) ? null : reader.GetString(7)), ServerCompletedAt = Time(reader.IsDBNull(8) ? null : reader.GetString(8)) });
        return stats;
    }
    public static void SaveStats(Settings settings, ReviewStats stats) { using var connection = OpenDatabase(settings); MigrateLegacy(settings, connection); SaveStats(connection, stats); }
    private static void SaveStats(SqliteConnection connection, ReviewStats stats)
    {
        using var transaction = connection.BeginTransaction(); using var clear = connection.CreateCommand(); clear.Transaction = transaction; clear.CommandText = "DELETE FROM review_stats"; clear.ExecuteNonQuery();
        foreach (var record in stats.Records)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO review_stats VALUES ($id,$type,$title,$author,$post,$queued,$first,$observed,$server)";
            command.Parameters.AddWithValue("$id", record.Id); command.Parameters.AddWithValue("$type", record.Type); command.Parameters.AddWithValue("$title", record.Title); command.Parameters.AddWithValue("$author", record.Author);
            command.Parameters.AddWithValue("$post", (object?)Text(record.PostCreatedAt) ?? DBNull.Value); command.Parameters.AddWithValue("$queued", (object?)Text(record.ReviewQueuedAt) ?? DBNull.Value); command.Parameters.AddWithValue("$first", record.FirstSeen.ToString("O")); command.Parameters.AddWithValue("$observed", (object?)Text(record.CompletedAt) ?? DBNull.Value); command.Parameters.AddWithValue("$server", (object?)Text(record.ServerCompletedAt) ?? DBNull.Value); command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public static DeliveryState LoadState(Settings settings)
    {
        using var connection = OpenDatabase(settings); MigrateLegacy(settings, connection); var state = new DeliveryState();
        using (var command = connection.CreateCommand()) { command.CommandText = "SELECT channel, reviewable_id FROM delivered"; using var reader = command.ExecuteReader(); while (reader.Read()) { string channel = reader.GetString(0); if (!state.Delivered.TryGetValue(channel, out var ids)) state.Delivered[channel] = ids = new(); ids.Add(reader.GetInt64(1)); } }
        using (var command = connection.CreateCommand()) { command.CommandText = "SELECT channel, sent_at FROM reminders"; using var reader = command.ExecuteReader(); while (reader.Read()) if (Time(reader.GetString(1)) is DateTimeOffset time) state.LastReminder[reader.GetString(0)] = time; }
        return state;
    }
    public static void SaveState(Settings settings, DeliveryState state) { using var connection = OpenDatabase(settings); MigrateLegacy(settings, connection); SaveState(connection, state); }
    private static void SaveState(SqliteConnection connection, DeliveryState state)
    {
        using var transaction = connection.BeginTransaction(); using var clear = connection.CreateCommand(); clear.Transaction = transaction; clear.CommandText = "DELETE FROM delivered; DELETE FROM reminders;"; clear.ExecuteNonQuery();
        foreach (var (channel, ids) in state.Delivered) foreach (long id in ids) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "INSERT INTO delivered VALUES ($channel, $id)"; command.Parameters.AddWithValue("$channel", channel); command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery(); }
        foreach (var (channel, sentAt) in state.LastReminder) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "INSERT INTO reminders VALUES ($channel, $time)"; command.Parameters.AddWithValue("$channel", channel); command.Parameters.AddWithValue("$time", sentAt.ToString("O")); command.ExecuteNonQuery(); }
        transaction.Commit();
    }
    public static void AtomicWrite(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, path, true);
    }
}

internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    public static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool success = protect ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException("Windows 凭据加密／解密失败。");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
}

public sealed class AppLog
{
    public event Action<string>? Added;
    private readonly Queue<string> lines = new();
    public IEnumerable<string> Lines => lines.ToArray();
    public void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        try
        {
            string directory = Path.Combine(Storage.DirectoryPath, "logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024)
                File.Move(path, Path.Combine(directory, DateTime.Now.ToString("yyyy-MM-dd-HHmmss-ffff") + ".log"));
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            foreach (string old in Directory.GetFiles(directory, "*.log").Where(x => File.GetLastWriteTime(x) < DateTime.Now.AddDays(-30))) File.Delete(old);
        }
        catch { line += " [日志文件写入失败，请检查目录权限和磁盘空间]"; }
        lines.Enqueue(line);
        while (lines.Count > 2000) lines.Dequeue();
        Added?.Invoke(line);
    }
}

