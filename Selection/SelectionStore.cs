using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.State;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.DialogueBoost.Selection;

/// <summary>
/// Stores what the user chose to cover, and is the only thing allowed to.
/// </summary>
/// <remarks>
/// The selection used to live in the plugin's XML configuration, where three unsynchronised writers
/// read-modify-wrote the whole document and any overlap silently lost one of them — the reported
/// "I saved, and the task still says nothing is selected". It has its own document here, so a
/// save of unrelated settings cannot reach it and two selection writes serialise in SQLite instead
/// of racing.
/// </remarks>
public sealed class SelectionStore
{
    private readonly PluginDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="SelectionStore"/> class.
    /// </summary>
    /// <param name="database">The plugin's SQLite file.</param>
    public SelectionStore(PluginDatabase database)
    {
        _database = database;
        Initialize();
    }

    /// <summary>
    /// Reads the stored selection.
    /// </summary>
    public async Task<LibrarySelection> GetAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return Read(connection, transaction: null);
    }

    /// <summary>
    /// Replaces the selection with exactly these nodes, in one transaction, and returns what is
    /// stored afterwards.
    /// </summary>
    /// <param name="paths">
    /// The rows the user chose. Duplicates collapse; an empty set clears the selection.
    /// </param>
    /// <param name="coverNewMedia">The new value, or <c>null</c> to leave it as it is.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The canonical stored state — never the caller's own input echoed back.</returns>
    public async Task<LibrarySelection> ReplaceAsync(
        IReadOnlyCollection<ScopePath> paths,
        bool? coverNewMedia = null,
        CancellationToken cancellationToken = default)
    {
        var wanted = (paths ?? Array.Empty<ScopePath>())
            .Where(path => path is not null && !path.IsRoot)
            .Distinct()
            .ToList();

        using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

        RemoveScopesOtherThan(connection, transaction, wanted);
        AddMissingScopes(connection, transaction, wanted, DateTime.UtcNow);
        WriteSettings(connection, transaction, coverNewMedia);

        var stored = Read(connection, transaction);
        transaction.Commit();
        return stored;
    }

    /// <summary>
    /// Replaces the stored scopes with these, watermarks and all, without counting as a save.
    /// </summary>
    /// <remarks>
    /// For <see cref="SelectionUpgrade"/>, which rewrites how a scope is *spelled* and must not
    /// change what it means. Two things follow: each scope keeps the moment it was chosen, rather
    /// than being treated as newly added and un-freezing itself; and <c>updated_at_utc</c> is left
    /// alone, because it is also the record of the last time the user saw the library list, and
    /// moving it would quietly mark every library added since then as already seen.
    /// </remarks>
    public async Task<LibrarySelection> RewriteAsync(
        IReadOnlyCollection<SelectionScope> scopes,
        CancellationToken cancellationToken = default)
    {
        using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM selection_scopes;";
            clear.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = @"
                INSERT INTO selection_scopes (path, added_at_utc)
                VALUES ($path, $added_at_utc)
                ON CONFLICT(path) DO NOTHING;
            ";
            var path = insert.Parameters.Add("$path", SqliteType.Text);
            var addedAt = insert.Parameters.Add("$added_at_utc", SqliteType.Text);

            foreach (var scope in scopes)
            {
                path.Value = scope.Path.ToString();
                addedAt.Value = Stamp(scope.AddedAtUtc);
                insert.ExecuteNonQuery();
            }
        }

        var stored = Read(connection, transaction);
        transaction.Commit();
        return stored;
    }

    private void Initialize()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        // The settings row doubles as the "this plugin owns its selection now" marker: while it is
        // absent, a selection stored by an older version has still to be imported.
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS selection_scopes (
                path          TEXT NOT NULL PRIMARY KEY,
                added_at_utc  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS selection_settings (
                id               INTEGER NOT NULL PRIMARY KEY CHECK (id = 0),
                cover_new_media  INTEGER NOT NULL,
                updated_at_utc   TEXT    NOT NULL
            );
        ";
        command.ExecuteNonQuery();

        RenameLegacyColumn(connection);
    }

    /// <summary>
    /// A selection stored before rows had paths kept one item id per row. The column is renamed
    /// rather than rebuilt, so the ids and their watermarks survive untouched: a bare id is a valid
    /// path meaning "exactly this item", which is precisely what it meant before, and
    /// <c>SelectionUpgrade</c> promotes it to a real tree path once the library is up.
    /// </summary>
    private static void RenameLegacyColumn(SqliteConnection connection)
    {
        using var columns = connection.CreateCommand();
        columns.CommandText = "SELECT 1 FROM pragma_table_info('selection_scopes') WHERE name = 'item_id';";
        if (columns.ExecuteScalar() is null)
        {
            return;
        }

        using var rename = connection.CreateCommand();
        rename.CommandText = "ALTER TABLE selection_scopes RENAME COLUMN item_id TO path;";
        rename.ExecuteNonQuery();
    }

    private static void RemoveScopesOtherThan(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ScopePath> wanted)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        if (wanted.Count == 0)
        {
            command.CommandText = "DELETE FROM selection_scopes;";
        }
        else
        {
            var names = new List<string>(wanted.Count);
            for (int i = 0; i < wanted.Count; i++)
            {
                string name = "$id" + i.ToString(CultureInfo.InvariantCulture);
                names.Add(name);
                command.Parameters.AddWithValue(name, wanted[i].ToString());
            }

            command.CommandText = $"DELETE FROM selection_scopes WHERE path NOT IN ({string.Join(", ", names)});";
        }

        command.ExecuteNonQuery();
    }

    private static void AddMissingScopes(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ScopePath> wanted,
        DateTime addedAtUtc)
    {
        if (wanted.Count == 0)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // DO NOTHING, not DO UPDATE: re-saving a selection must not reset the watermark of a scope
        // that was already there, or every save would silently un-freeze it.
        command.CommandText = @"
            INSERT INTO selection_scopes (path, added_at_utc)
            VALUES ($path, $added_at_utc)
            ON CONFLICT(path) DO NOTHING;
        ";
        var path = command.Parameters.Add("$path", SqliteType.Text);
        command.Parameters.AddWithValue("$added_at_utc", Stamp(addedAtUtc));

        foreach (var wantedPath in wanted)
        {
            path.Value = wantedPath.ToString();
            command.ExecuteNonQuery();
        }
    }

    private static void WriteSettings(
        SqliteConnection connection,
        SqliteTransaction transaction,
        bool? coverNewMedia)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // "Leave it as it is" still has to write something the first time, so the two statements
        // differ only in whether an existing value is overwritten or kept.
        command.CommandText = coverNewMedia.HasValue
            ? @"
                INSERT INTO selection_settings (id, cover_new_media, updated_at_utc)
                VALUES (0, $cover_new_media, $updated_at_utc)
                ON CONFLICT(id) DO UPDATE SET
                    cover_new_media = excluded.cover_new_media,
                    updated_at_utc  = excluded.updated_at_utc;
              "
            : @"
                INSERT INTO selection_settings (id, cover_new_media, updated_at_utc)
                VALUES (0, $cover_new_media, $updated_at_utc)
                ON CONFLICT(id) DO UPDATE SET
                    updated_at_utc = excluded.updated_at_utc;
              ";

        bool value = coverNewMedia ?? LibrarySelection.CoverNewMediaDefault;
        command.Parameters.AddWithValue("$cover_new_media", value ? 1 : 0);
        command.Parameters.AddWithValue("$updated_at_utc", Stamp(DateTime.UtcNow));
        command.ExecuteNonQuery();
    }

    private static LibrarySelection Read(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var scopes = new List<SelectionScope>();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT path, added_at_utc FROM selection_scopes ORDER BY added_at_utc, path;";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = ScopePath.Parse(reader.GetString(0));
                if (!path.IsRoot)
                {
                    scopes.Add(new SelectionScope(path, ParseStamp(reader.GetString(1))));
                }
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT cover_new_media, updated_at_utc FROM selection_settings WHERE id = 0;";

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return new LibrarySelection(scopes, reader.GetInt64(0) != 0, ParseStamp(reader.GetString(1)));
            }
        }

        return LibrarySelection.Empty with { Scopes = scopes };
    }

    private static string Stamp(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseStamp(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.MinValue;
}
