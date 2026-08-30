using System.Text.Json;
using System.Text.Json.Serialization;
using SteamSwitchboard.Models;

namespace SteamSwitchboard.Services;

public sealed class StateStore
{
    public const int MaximumStateFileBytes = 4 * 1024 * 1024;
    private const int MaximumCorruptBackups = 3;
    private const int MaximumJsonElements = 100_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _stateFile;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StateStore(string stateFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFile);
        if (!LocalPathPolicy.TryNormalizeLocalPath(
                stateFile,
                out var normalizedStateFile,
                requireExisting: false))
        {
            throw new ArgumentException(
                "The settings file must use a safe local path.",
                nameof(stateFile));
        }

        _stateFile = normalizedStateFile;
    }

    public async Task<PersistedState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!LocalPathPolicy.TryNormalizeLocalPath(
                    _stateFile,
                    out _,
                    requireExisting: false))
            {
                throw new InvalidDataException(
                    "The settings file cannot use links or remote storage.");
            }

            if (!File.Exists(_stateFile))
            {
                return new PersistedState();
            }

            if (!LocalPathPolicy.TryNormalizeLocalPath(_stateFile, out _))
            {
                throw new InvalidDataException(
                    "The settings file cannot use links or remote storage.");
            }

            await using var stream = new FileStream(
                _stateFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (stream.Length > MaximumStateFileBytes)
            {
                throw new InvalidDataException("The settings file is too large.");
            }

            using var boundedState = new MemoryStream(capacity: (int)stream.Length);
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                if (boundedState.Length + bytesRead > MaximumStateFileBytes)
                {
                    throw new InvalidDataException("The settings file is too large.");
                }

                await boundedState.WriteAsync(
                    buffer.AsMemory(0, bytesRead),
                    cancellationToken).ConfigureAwait(false);
            }

            boundedState.Position = 0;
            using (var document = await JsonDocument.ParseAsync(
                boundedState,
                new JsonDocumentOptions
                {
                    MaxDepth = JsonOptions.MaxDepth,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                },
                cancellationToken).ConfigureAwait(false))
            {
                var elementCount = 0;
                ValidateJsonShape(document.RootElement, ref elementCount);
            }

            boundedState.Position = 0;
            var state = await JsonSerializer.DeserializeAsync<PersistedState>(
                boundedState,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            if (state is null
                || state.SchemaVersion < 1
                || state.SchemaVersion > PersistedState.CurrentSchemaVersion)
            {
                throw new InvalidDataException("The settings file uses an unsupported format.");
            }

            PersistedStateValidator.ValidateAndNormalize(state);
            state.SchemaVersion = PersistedState.CurrentSchemaVersion;
            return state;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            TryPreserveCorruptState();
            throw new InvalidDataException(
                "Switchboard settings were damaged. A recovery copy was preserved.",
                exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(PersistedState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        PersistedStateValidator.ValidateAndNormalize(state);
        state.SchemaVersion = PersistedState.CurrentSchemaVersion;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_stateFile)
                ?? throw new InvalidOperationException("The settings path has no parent folder.");
            Directory.CreateDirectory(directory);
            if (!LocalPathPolicy.TryNormalizeLocalPath(directory, out _))
            {
                throw new InvalidOperationException(
                    "The settings folder cannot use links or remote storage.");
            }

            if (File.Exists(_stateFile)
                && !LocalPathPolicy.TryNormalizeLocalPath(_stateFile, out _))
            {
                throw new InvalidOperationException(
                    "The settings file cannot use links or remote storage.");
            }

            var temporaryFile = Path.Combine(
                directory,
                $".{Path.GetFileName(_stateFile)}.{Guid.NewGuid():N}.tmp");

            try
            {
                long serializedLength;
                await using (var stream = new FileStream(
                    temporaryFile,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        state,
                        JsonOptions,
                        cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    serializedLength = stream.Length;
                }

                if (serializedLength > MaximumStateFileBytes)
                {
                    throw new InvalidOperationException("Switchboard has too much profile metadata to save safely.");
                }

                File.Move(temporaryFile, _stateFile, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryFile))
                {
                    File.Delete(temporaryFile);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateJsonShape(JsonElement element, ref int elementCount)
    {
        elementCount++;
        if (elementCount > MaximumJsonElements)
        {
            throw new InvalidDataException(
                "The settings file contains too many JSON elements.");
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        "The settings file contains a duplicate JSON property.");
                }

                ValidateJsonShape(property.Value, ref elementCount);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateJsonShape(item, ref elementCount);
            }
        }
    }

    private void TryPreserveCorruptState()
    {
        try
        {
            if (!LocalPathPolicy.TryNormalizeLocalPath(
                    _stateFile,
                    out _,
                    requireExisting: false)
                || !File.Exists(_stateFile)
                || !LocalPathPolicy.TryNormalizeLocalPath(_stateFile, out _))
            {
                return;
            }

            var directory = Path.GetDirectoryName(_stateFile)!;
            var backup = Path.Combine(
                directory,
                $"state.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}.json");
            File.Copy(_stateFile, backup, overwrite: false);

            var expiredBackups = Directory
                .EnumerateFiles(directory, "state.corrupt.*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetCreationTimeUtc)
                .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .Skip(MaximumCorruptBackups)
                .ToArray();
            foreach (var expiredBackup in expiredBackups)
            {
                File.Delete(expiredBackup);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Recovery copies are best effort and must not hide the original validation error.
        }
    }
}
