using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.DialogueBoost.State;

/// <summary>
/// The plugin's SQLite file: where it lives, and how a connection to it is opened.
/// </summary>
/// <remarks>
/// One file holds every table the plugin owns, so "open a connection" is the one thing every
/// repository needs and none of them should re-derive. Pragmas are applied per connection on
/// purpose: <c>busy_timeout</c> is a connection setting rather than a database one, so setting it
/// only while creating the schema — as this plugin used to — leaves every later connection failing
/// immediately on a locked database instead of waiting for it.
/// </remarks>
public sealed class PluginDatabase
{
    private const int BusyTimeoutMs = 5000;

    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginDatabase"/> class in the plugin's own
    /// data folder.
    /// </summary>
    public PluginDatabase()
        : this(DefaultPath())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginDatabase"/> class at an explicit path.
    /// </summary>
    /// <param name="path">Full path to the SQLite file. Its directory must already exist.</param>
    public PluginDatabase(string path)
    {
        Path = path;
        _connectionString = $"Data Source={path}";
    }

    /// <summary>
    /// Gets the full path of the SQLite file.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Opens a configured connection.
    /// </summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Configure(connection);
        return connection;
    }

    /// <summary>
    /// Opens a configured connection without blocking the calling thread.
    /// </summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        Configure(connection);
        return connection;
    }

    private static string DefaultPath()
    {
        string dataFolder = Plugin.Instance?.DataFolderPath
            ?? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "jellyfin",
                "plugins",
                "DialogueBoost");

        Directory.CreateDirectory(dataFolder);
        return System.IO.Path.Combine(dataFolder, "dialogue_boost.db");
    }

    private static void Configure(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        // WAL is a property of the file and survives, so re-stating it is a no-op after the first
        // connection; busy_timeout is per connection and has to be set every time.
        command.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA busy_timeout={BusyTimeoutMs};";
        command.ExecuteNonQuery();
    }
}
