using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Analysis;
using Jellyfin.Plugin.DialogueBoost.Configuration.Profiles;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.State;

public class ProcessingStateRepository
{
    private readonly PluginDatabase _database;
    private readonly ILogger<ProcessingStateRepository> _logger;

    /// <summary>
    /// The columns every read of <c>processed_items</c> selects, in the order
    /// <see cref="MapReaderToRecord"/> expects. One list, so adding a column is one edit.
    /// </summary>
    private const string RecordColumns =
        "item_id, profile_id, source_path, source_mtime_utc, source_size_bytes, " +
        "params_hash, profile_params_hash, sidecar_path, processed_languages, status, " +
        "skip_reason, processed_at_utc, exempt_from_cleanup";

    public ProcessingStateRepository(PluginDatabase database, ILogger<ProcessingStateRepository> logger)
    {
        _database = database;
        _logger = logger;
        InitializeDatabase();
    }

    public ProcessingStateRepository(string dbPath, ILogger<ProcessingStateRepository> logger)
        : this(new PluginDatabase(dbPath), logger)
    {
    }

    private void InitializeDatabase()
    {
        using var connection = _database.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS processed_items (
                item_id              TEXT    NOT NULL,
                profile_id           TEXT    NOT NULL,
                source_path          TEXT    NOT NULL,
                source_mtime_utc     INTEGER NOT NULL,
                source_size_bytes    INTEGER NOT NULL,
                params_hash          TEXT    NOT NULL,
                sidecar_path         TEXT    NOT NULL,
                processed_languages  TEXT    NOT NULL,
                status               TEXT    NOT NULL,
                skip_reason          TEXT,
                processed_at_utc     INTEGER NOT NULL,
                exempt_from_cleanup  INTEGER NOT NULL DEFAULT 0,
                profile_params_hash  TEXT,
                PRIMARY KEY (item_id, profile_id)
            );

            CREATE INDEX IF NOT EXISTS idx_processed_status ON processed_items(status);
            CREATE INDEX IF NOT EXISTS idx_processed_path ON processed_items(source_path);
        ";
        command.ExecuteNonQuery();

        // Columns added after the table shipped. Asked for by name rather than added blindly and
        // swallowing the error: a failed ALTER for any other reason should be seen, not hidden.
        EnsureColumn(connection, "exempt_from_cleanup", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "profile_params_hash", "TEXT");
    }

    /// <summary>
    /// Adds a column to <c>processed_items</c> if the table does not already have it.
    /// </summary>
    private static void EnsureColumn(SqliteConnection connection, string name, string definition)
    {
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('processed_items') WHERE name = $name;";
            probe.Parameters.AddWithValue("$name", name);
            if (Convert.ToInt32(probe.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE processed_items ADD COLUMN {name} {definition};";
        alter.ExecuteNonQuery();
    }

    public async Task<ProcessedItemRecord?> GetRecordAsync(string itemId, string profileId)
    {
        using var connection = await _database.OpenAsync().ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT " + RecordColumns + @"
            FROM processed_items
            WHERE item_id = $item_id AND profile_id = $profile_id;
        ";
        command.Parameters.AddWithValue("$item_id", itemId);
        command.Parameters.AddWithValue("$profile_id", profileId);

        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (await reader.ReadAsync().ConfigureAwait(false))
        {
            return MapReaderToRecord(reader);
        }

        return null;
    }

    public async Task SaveRecordAsync(ProcessedItemRecord record)
    {
        using var connection = await _database.OpenAsync().ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO processed_items (
                item_id, profile_id, source_path, source_mtime_utc, source_size_bytes,
                params_hash, profile_params_hash, sidecar_path, processed_languages, status,
                skip_reason, processed_at_utc, exempt_from_cleanup
            )
            VALUES (
                $item_id, $profile_id, $source_path, $source_mtime_utc, $source_size_bytes,
                $params_hash, $profile_params_hash, $sidecar_path, $processed_languages, $status,
                $skip_reason, $processed_at_utc, $exempt_from_cleanup
            )
            ON CONFLICT(item_id, profile_id) DO UPDATE SET
                source_path = excluded.source_path,
                source_mtime_utc = excluded.source_mtime_utc,
                source_size_bytes = excluded.source_size_bytes,
                params_hash = excluded.params_hash,
                profile_params_hash = excluded.profile_params_hash,
                sidecar_path = excluded.sidecar_path,
                processed_languages = excluded.processed_languages,
                status = excluded.status,
                skip_reason = excluded.skip_reason,
                processed_at_utc = excluded.processed_at_utc,
                exempt_from_cleanup = excluded.exempt_from_cleanup;
        ";

        command.Parameters.AddWithValue("$item_id", record.ItemId);
        command.Parameters.AddWithValue("$profile_id", record.ProfileId);
        command.Parameters.AddWithValue("$source_path", record.SourcePath);
        command.Parameters.AddWithValue("$source_mtime_utc", record.SourceMTimeUtc);
        command.Parameters.AddWithValue("$source_size_bytes", record.SourceSizeBytes);
        command.Parameters.AddWithValue("$params_hash", record.ParamsHash);
        command.Parameters.AddWithValue("$profile_params_hash", (object?)record.ProfileParamsHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$sidecar_path", record.SidecarPath);
        command.Parameters.AddWithValue("$processed_languages", JsonSerializer.Serialize(record.ProcessedLanguages));
        command.Parameters.AddWithValue("$status", record.Status);
        command.Parameters.AddWithValue("$skip_reason", (object?)record.SkipReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$processed_at_utc", record.ProcessedAtUtc);
        command.Parameters.AddWithValue("$exempt_from_cleanup", record.ExemptFromCleanup ? 1 : 0);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<bool> DeleteRecordAsync(string itemId, string profileId)
    {
        using var connection = await _database.OpenAsync().ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM processed_items WHERE item_id = $item_id AND profile_id = $profile_id;";
        command.Parameters.AddWithValue("$item_id", itemId);
        command.Parameters.AddWithValue("$profile_id", profileId);

        int rows = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<int> DeleteAllRecordsForItemAsync(string itemId)
    {
        using var connection = await _database.OpenAsync().ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM processed_items WHERE item_id = $item_id;";
        command.Parameters.AddWithValue("$item_id", itemId);

        return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<List<ProcessedItemRecord>> GetRecordsByProfileAsync(string profileId)
    {
        var results = new List<ProcessedItemRecord>();
        using var connection = await _database.OpenAsync().ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT " + RecordColumns + @"
            FROM processed_items
            WHERE profile_id = $profile_id;
        ";
        command.Parameters.AddWithValue("$profile_id", profileId);

        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            results.Add(MapReaderToRecord(reader));
        }

        return results;
    }

    public async Task<List<ProcessedItemRecord>> GetRecordsWithSidecarsAsync()
    {
        var results = new List<ProcessedItemRecord>();
        using var connection = await _database.OpenAsync().ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT " + RecordColumns + @"
            FROM processed_items
            WHERE status = 'Success' AND sidecar_path IS NOT NULL AND sidecar_path != '';
        ";

        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            results.Add(MapReaderToRecord(reader));
        }

        return results;
    }

    /// <summary>
    /// Every record, in no particular order.
    /// </summary>
    /// <remarks>
    /// For the sweep that removes everything the plugin has written: a record is the only thing
    /// that can still find a file whose profile has since been renamed, and a record whose item
    /// has left the library is still a row to clear. <c>GetHistoryAsync</c> is the paged form and
    /// answers a page of a table; this answers the table.
    /// </remarks>
    public async Task<List<ProcessedItemRecord>> GetAllRecordsAsync()
    {
        var results = new List<ProcessedItemRecord>();
        using var connection = await _database.OpenAsync().ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + RecordColumns + " FROM processed_items;";

        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            results.Add(MapReaderToRecord(reader));
        }

        return results;
    }

    /// <summary>Clears the table, and answers how many rows it held.</summary>
    public async Task<int> DeleteAllRecordsAsync()
    {
        using var connection = await _database.OpenAsync().ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM processed_items;";

        return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<List<ProcessedItemRecord>> GetHistoryAsync(int skip = 0, int take = 50, string? statusFilter = null)
    {
        var results = new List<ProcessedItemRecord>();
        using var connection = await _database.OpenAsync().ConfigureAwait(false);

        using var command = connection.CreateCommand();
        string sql = @"
            SELECT " + RecordColumns + @"
            FROM processed_items";

        if (!string.IsNullOrWhiteSpace(statusFilter))
        {
            sql += " WHERE status = $statusFilter";
            command.Parameters.AddWithValue("$statusFilter", statusFilter);
        }

        sql += " ORDER BY processed_at_utc DESC LIMIT $take OFFSET $skip;";
        command.Parameters.AddWithValue("$take", take);
        command.Parameters.AddWithValue("$skip", skip);

        command.CommandText = sql;

        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            results.Add(MapReaderToRecord(reader));
        }

        return results;
    }

    public async Task UpdateSourcePathIfRenamedAsync(string itemId, string newSourcePath)
    {
        using var connection = await _database.OpenAsync().ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE processed_items SET source_path = $newSourcePath WHERE item_id = $itemId;";
        command.Parameters.AddWithValue("$newSourcePath", newSourcePath);
        command.Parameters.AddWithValue("$itemId", itemId);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<bool> SetExemptAsync(string itemId, string profileId, bool exempt)
    {
        using var connection = await _database.OpenAsync().ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE processed_items SET exempt_from_cleanup = $exempt WHERE item_id = $itemId AND profile_id = $profileId;";
        command.Parameters.AddWithValue("$exempt", exempt ? 1 : 0);
        command.Parameters.AddWithValue("$itemId", itemId);
        command.Parameters.AddWithValue("$profileId", profileId);

        int rows = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        return rows > 0;
    }

    private static ProcessedItemRecord MapReaderToRecord(SqliteDataReader reader)
    {
        string langsJson = reader.GetString( reader.GetOrdinal("processed_languages") );
        var langs = JsonSerializer.Deserialize<List<string>>(langsJson) ?? new List<string>();

        int exemptCol = reader.GetOrdinal("exempt_from_cleanup");
        bool isExempt = !reader.IsDBNull(exemptCol) && reader.GetInt32(exemptCol) != 0;

        return new ProcessedItemRecord
        {
            ItemId = reader.GetString(reader.GetOrdinal("item_id")),
            ProfileId = reader.GetString(reader.GetOrdinal("profile_id")),
            SourcePath = reader.GetString(reader.GetOrdinal("source_path")),
            SourceMTimeUtc = reader.GetInt64(reader.GetOrdinal("source_mtime_utc")),
            SourceSizeBytes = reader.GetInt64(reader.GetOrdinal("source_size_bytes")),
            ParamsHash = reader.GetString(reader.GetOrdinal("params_hash")),
            ProfileParamsHash = reader.IsDBNull(reader.GetOrdinal("profile_params_hash"))
                ? null
                : reader.GetString(reader.GetOrdinal("profile_params_hash")),
            SidecarPath = reader.GetString(reader.GetOrdinal("sidecar_path")),
            ProcessedLanguages = langs,
            Status = reader.GetString(reader.GetOrdinal("status")),
            SkipReason = reader.IsDBNull(reader.GetOrdinal("skip_reason")) ? null : reader.GetString(reader.GetOrdinal("skip_reason")),
            ProcessedAtUtc = reader.GetInt64(reader.GetOrdinal("processed_at_utc")),
            ExemptFromCleanup = isExempt
        };
    }

    /// <summary>
    /// The hash that decides whether a sidecar is still current: the profile's own parameters,
    /// plus a signature of the source's audio tracks. A mismatch means reprocess.
    /// </summary>
    public static string ComputeParamsHash(
        BaseProfileConfig profile,
        List<AudioStreamInfo> sourceStreams,
        bool claimsDefaultTrack = false)
    {
        var sb = new StringBuilder();
        AppendProfileParams(sb, profile, claimsDefaultTrack);

        // The ordinal among the source's own audio tracks, never the absolute container index.
        // Publishing a sidecar renumbers the container — measured on a fresh item, embedded audio
        // moved from 1,2 to 3,4 the moment the sidecar became visible — so a hash over the absolute
        // index changed for a file nobody had touched, and the next run re-encoded the whole
        // library once. The ordinal is also what `-map 0:a:N` means.
        sb.Append("SourceStreams:[");
        foreach (var s in sourceStreams)
        {
            sb.Append($"({s.AudioIndex}:{s.Language}:{s.Codec}:{s.Channels}:{s.ChannelLayout}:{s.BitrateBps}:{s.IsDefault}),");
        }

        sb.Append("]");

        return Sha256Hex(sb.ToString());
    }

    /// <summary>
    /// The profile half of <see cref="ComputeParamsHash"/> on its own.
    /// </summary>
    /// <remarks>
    /// Stored beside the full hash so that a settings change can be spotted without re-probing
    /// every source file. The full hash cannot answer that question on its own: it folds in the
    /// source's track signature, which costs an ffprobe per item to reproduce. Comparing this half
    /// against a record whose source mtime and size are unchanged reaches the same answer, because
    /// an unchanged file has an unchanged track signature.
    /// </remarks>
    public static string ComputeProfileParamsHash(BaseProfileConfig profile, bool claimsDefaultTrack = false)
    {
        var sb = new StringBuilder();
        AppendProfileParams(sb, profile, claimsDefaultTrack);
        return Sha256Hex(sb.ToString());
    }

    /// <summary>
    /// Bumped whenever the encode a profile's settings build changes — the filter graph, the codec
    /// or the rate.
    /// </summary>
    /// <remarks>
    /// The hash covers the settings and the source, which is enough while the same settings over
    /// the same source always produce the same command. When the *construction* is what changed,
    /// nothing either half reads has moved, so every sidecar written by the old code reads as
    /// current and is never rewritten — which is how a defect can be fixed in the code and left
    /// standing in the library.
    /// <para>
    /// The rate belongs here for the same reason the graph does, and it took a live run to notice:
    /// <see cref="Analysis.SidecarBitrate"/> reading a source's codec differently is invisible to
    /// both halves, because what they carry is the source's own rate and the *mode*, never the
    /// number the two of them resolve to.
    /// </para>
    /// <para>
    /// 2 — the centre-gain branch names its input channels by position rather than by the layout's
    /// channel names. Every 5.1 sidecar written by revision 1 carries silent surrounds and has to
    /// be re-encoded.
    /// </para>
    /// <para>
    /// Deliberately still 2 after the format work of 2026-08-29, which changed the codec above 5.1,
    /// the layouts the centre branch accepts, the branch a layout with no centre takes, and the
    /// rate an uncompressed source gets. None of it moves a single command this library produces —
    /// its 481 six-channel and 54 stereo tracks build the same graph, codec and rate as before — so
    /// a bump would have bought one full re-encode and no changed byte. Anything that does move an
    /// output here has to bump it.
    /// </para>
    /// </remarks>
    private const int EncodeRevision = 2;

    /// <summary>
    /// Writes every profile parameter that changes the output. Shared by both hashes above so the
    /// two can never drift apart.
    /// </summary>
    private static void AppendProfileParams(StringBuilder sb, BaseProfileConfig profile, bool claimsDefaultTrack)
    {
        // The property name is what the old records hashed; renaming the constant must not
        // renumber the library.
        sb.Append($"FilterGraphRevision:{EncodeRevision};");
        sb.Append($"ProfileId:{profile.Id};");
        sb.Append($"SetAsDefaultTrack:{profile.SetAsDefaultTrack};");
        // Not the same fact as the switch above: the claim is resolved against the other profiles,
        // so disabling the one that held it moves this one's filename without its own settings
        // having changed. The forecast reads this hash to say whether a written track still stands.
        sb.Append($"ClaimsDefaultTrack:{claimsDefaultTrack};");
        // Fix #9: Include SidecarNamingMarker so marker renames trigger reprocessing
        sb.Append($"SidecarNamingMarker:{profile.SidecarNamingMarker};");
        sb.Append($"ProcessLanguages:{string.Join(",", profile.ProcessLanguages)};");
        // The rate a mode resolves to also depends on the source, which the stream signature above
        // carries — including its bitrate, which is exactly what BitrateMode.Auto follows.
        sb.Append($"BitrateMode:{profile.BitrateMode};");
        sb.Append($"BitrateKbps:{profile.BitrateKbps.ToString(CultureInfo.InvariantCulture)};");

        // Fix #16: Use InvariantCulture for all doubles to avoid locale-dependent hash changes
        if (profile is DialogueBoostProfile dbProf)
        {
            sb.Append($"GainDb:{dbProf.CenterChannelGainDb.ToString(CultureInfo.InvariantCulture)};");
        }
        else if (profile is NightModeProfile nmProf)
        {
            sb.Append($"DynGain:{nmProf.DynaudnormGain.ToString(CultureInfo.InvariantCulture)};");
        }
        else if (profile is SpeechProfile spProf)
        {
            sb.Append($"SpeechPeak:{spProf.Peak.ToString(CultureInfo.InvariantCulture)};");
        }
        else if (profile is Ebur128Profile ebProf)
        {
            sb.Append($"EbuI:{ebProf.I.ToString(CultureInfo.InvariantCulture)};EbuTP:{ebProf.TP.ToString(CultureInfo.InvariantCulture)};EbuLRA:{ebProf.LRA.ToString(CultureInfo.InvariantCulture)};");
        }
        else if (profile is CustomProfile custProf)
        {
            sb.Append($"Filter:{custProf.FilterString};Codec:{custProf.Codec};Bitrate:{custProf.Bitrate};");
        }

    }

    private static string Sha256Hex(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
