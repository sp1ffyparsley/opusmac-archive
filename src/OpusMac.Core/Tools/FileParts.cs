using OpusMac.Core.Search;
using System.Text;

namespace OpusMac.Core.Tools;

public static class FileParts
{
    public static Task<int> SplitAsync(string source, string expectedSourceIdentity, string directory,
        string expectedParentIdentity, long chunkBytes, bool uuencode, IProgress<ToolProgress>? progress, CancellationToken token) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        if (chunkBytes <= 0) throw new ArgumentOutOfRangeException(nameof(chunkBytes));
        ToolPath.RequireNoLinks(source);
        var original = ToolFileSnapshot.Read(source);
        if (original.IsDirectory || original.IsLink || original.NativeIdentity != expectedSourceIdentity)
            throw new IOException($"The reviewed file changed before splitting: {source}");
        if (original.Length <= chunkBytes)
            throw new IOException("Choose a chunk size smaller than the source file.");
        var count = 1 + (original.Length - 1) / chunkBytes;
        if (count > 10_000) throw new IOException("Choose a larger chunk size. Split File supports up to 10,000 pieces.");
        var parent = ToolPath.Full(directory);
        var parentIdentity = ArchiveService.CaptureDestinationParentIdentity(parent, expectedParentIdentity);
        var filename = Path.GetFileName(original.Path);
        var targets = Enumerable.Range(1, (int)count)
            .Select(index => ArchiveService.PrepareTarget(Path.Combine(parent, filename + "." +
                index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture))))
            .ToArray();
        var staged = new List<(string Path, string? Identity)>(targets.Length);
        var committed = new List<(string Path, string? Identity)>(targets.Length);
        var buffer = new byte[131072];
        long bytes = 0;
        try
        {
            await using (var input = new FileStream(original.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (!original.StillMatches()) throw new IOException("The source changed while it was opened.");
                for (var index = 0; index < targets.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    ArchiveService.RequireDestinationParentIdentity(parent, parentIdentity);
                    var stage = Path.Combine(parent, ".opusmac-split-" + Guid.NewGuid().ToString("N") + ".tmp");
                    await using var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                        buffer.Length, FileOptions.Asynchronous);
                    var identity = ListaryClient.ReadNativeItemIdentity(stage);
                    // Track the private sibling immediately so cancellation during a write can clean it up.
                    staged.Add((stage, identity));
                    if (identity is null)
                    {
                        await output.DisposeAsync().ConfigureAwait(false);
                        File.Delete(stage);
                        throw new IOException("The temporary split piece could not be identified.");
                    }
                    var remaining = Math.Min(chunkBytes, original.Length - bytes);
                    void Report(int read)
                    {
                        bytes += read;
                        progress?.Report(new ToolProgress("Splitting " + filename, index, targets.Length, bytes, original.Length));
                    }
                    if (uuencode)
                    {
                        await EncodePieceAsync(input, output, Path.GetFileName(targets[index]), remaining, Report, token)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        while (remaining > 0)
                        {
                            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token)
                                .ConfigureAwait(false);
                            if (read == 0) throw new EndOfStreamException("The source became shorter while splitting.");
                            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                            remaining -= read;
                            Report(read);
                        }
                    }
                    await output.FlushAsync(token).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                if (!original.StillMatches()) throw new IOException("The source changed while splitting.");
            }
            progress?.Report(new ToolProgress("Split pieces ready to commit", targets.Length, targets.Length, bytes, original.Length));
            token.ThrowIfCancellationRequested();
            for (var index = 0; index < targets.Length; index++)
            {
                ArchiveService.RequireDestinationParentIdentity(parent, parentIdentity);
                var piece = staged[index];
                if (ListaryClient.ReadNativeItemIdentity(piece.Path) != piece.Identity)
                    throw new IOException("A staged split piece changed before commit.");
                File.Move(piece.Path, targets[index]);
                committed.Add((targets[index], piece.Identity));
            }
            return targets.Length;
        }
        catch (Exception failure)
        {
            var cleanupFailures = new List<Exception>();
            foreach (var (path, identity) in committed.Concat(staged))
            {
                try { DeleteOwnedPiece(path, identity); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    cleanupFailures.Add(new IOException($"Could not clean up split output: {path}", error));
                }
            }
            if (cleanupFailures.Count > 0)
                throw new IOException($"{failure.Message} Some split output could not be cleaned up. {cleanupFailures[0].Message}",
                    new AggregateException(cleanupFailures.Prepend(failure)));
            throw;
        }
    }, token);

    private static void DeleteOwnedPiece(string path, string? identity)
    {
        if (identity is null) return;
        var actual = ListaryClient.ReadNativeItemIdentity(path);
        if (actual == identity) File.Delete(path);
        else if (actual is null && ToolPath.Exists(path))
            throw new IOException($"Could not verify the identity of the remaining split output: {path}");
    }

    public static Task<long> JoinAsync(IReadOnlyList<(string Path, string Identity)> sources, string destination,
        string expectedParentIdentity, bool uudecode, IProgress<ToolProgress>? progress, CancellationToken token) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        if (sources.Count < 2) throw new ArgumentException("Add at least two files to join.", nameof(sources));
        var target = ArchiveService.PrepareTarget(destination);
        var parent = Path.GetDirectoryName(target)!;
        var parentIdentity = ArchiveService.CaptureDestinationParentIdentity(parent, expectedParentIdentity);
        var parts = sources.Select(source =>
        {
            ToolPath.RequireNoLinks(source.Path);
            var snapshot = ToolFileSnapshot.Read(source.Path);
            if (snapshot.IsDirectory || snapshot.IsLink || snapshot.NativeIdentity != source.Identity)
                throw new IOException($"The reviewed file changed before joining: {source.Path}");
            return snapshot;
        }).ToArray();
        var totalBytes = parts.Sum(part => part.Length);
        var stage = Path.Combine(parent, ".opusmac-join-" + Guid.NewGuid().ToString("N") + ".tmp");
        var ownsStage = false;
        long bytes = 0;
        long outputLength = 0;
        var decoder = uudecode ? new UuDecoder() : null;
        try
        {
            ArchiveService.RequireDestinationParentIdentity(parent, parentIdentity);
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            {
                ownsStage = true;
                for (var index = 0; index < parts.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var part = parts[index];
                    ToolPath.RequireNoLinks(part.Path);
                    if (!part.StillMatches()) throw new IOException($"The file changed before joining: {part.Path}");
                    await using var input = new FileStream(part.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (!part.StillMatches()) throw new IOException($"The file changed while it was opened: {part.Path}");
                    void Report(int count)
                    {
                        bytes += count;
                        progress?.Report(new ToolProgress("Joining " + Path.GetFileName(part.Path), index, parts.Length, bytes, totalBytes));
                    }
                    if (decoder is null)
                        await ArchiveService.CopyBoundedAsync(input, output, part.Length, token, Report, computeCrc: false).ConfigureAwait(false);
                    else
                        await decoder.AppendAsync(input, output, part.Length, token).ConfigureAwait(false);
                    if (input.Position != part.Length || !part.StillMatches())
                        throw new IOException($"The file changed while joining: {part.Path}");
                    if (decoder is not null)
                    {
                        bytes += part.Length;
                        progress?.Report(new ToolProgress("Decoded " + Path.GetFileName(part.Path), index + 1, parts.Length, bytes, totalBytes));
                    }
                }
                decoder?.Finish();
                outputLength = output.Position;
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            ArchiveService.RequireDestinationParentIdentity(parent, parentIdentity);
            File.Move(stage, target);
            ownsStage = false;
            return outputLength;
        }
        finally
        {
            if (ownsStage) File.Delete(stage);
        }
    }, token);

    private static async Task EncodePieceAsync(Stream input, Stream output, string name, long length,
        Action<int> report, CancellationToken token)
    {
        await output.WriteAsync(Encoding.UTF8.GetBytes($"begin 644 {name}\r\n"), token).ConfigureAwait(false);
        var inputRow = new byte[45];
        var line = new byte[63];
        while (length > 0)
        {
            var count = (int)Math.Min(inputRow.Length, length);
            Array.Clear(inputRow);
            await input.ReadExactlyAsync(inputRow.AsMemory(0, count), token).ConfigureAwait(false);
            line[0] = UuCharacter(count);
            var position = 1;
            for (var index = 0; index < count; index += 3)
            {
                var a = inputRow[index];
                var b = inputRow[index + 1];
                var c = inputRow[index + 2];
                line[position++] = UuCharacter(a >> 2);
                line[position++] = UuCharacter((a << 4 | b >> 4) & 63);
                line[position++] = UuCharacter((b << 2 | c >> 6) & 63);
                line[position++] = UuCharacter(c & 63);
            }
            line[position++] = (byte)'\r';
            line[position++] = (byte)'\n';
            await output.WriteAsync(line.AsMemory(0, position), token).ConfigureAwait(false);
            length -= count;
            report(count);
        }
        await output.WriteAsync("`\r\nend\r\n"u8.ToArray(), token).ConfigureAwait(false);
    }

    private static byte UuCharacter(int value) => (byte)(value == 0 ? '`' : value + ' ');

    private sealed class UuDecoder
    {
        private enum State { AwaitingHeader, Data, AwaitingEnd }
        private State _state;
        private bool _completed;
        private readonly byte[] _line = new byte[1024];
        private readonly byte[] _decoded = new byte[45];
        private int _length;

        public async Task AppendAsync(Stream input, Stream output, long expectedLength, CancellationToken token)
        {
            var buffer = new byte[8192];
            while (true)
            {
                var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0) break;
                if (input.Position > expectedLength) throw new IOException("A UUencoded source grew while joining.");
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] == '\n')
                    {
                        await WriteLineAsync(output, token).ConfigureAwait(false);
                        _length = 0;
                    }
                    else
                    {
                        if (_length == _line.Length) throw new InvalidDataException("A UUencoded line exceeds the supported length.");
                        _line[_length++] = buffer[index];
                    }
                }
            }
        }

        public void Finish()
        {
            // A final terminator can lack a trailing newline.
            if (_length != 0 && (_state != State.AwaitingEnd || ParseLine() != 0))
                throw new InvalidDataException("The UUencoded input ends in a partial line.");
            if (!_completed || _state != State.AwaitingHeader)
                throw new InvalidDataException("The join inputs do not contain a complete UUencoded block.");
        }

        private async ValueTask WriteLineAsync(Stream output, CancellationToken token)
        {
            var bytes = ParseLine();
            if (bytes != 0) await output.WriteAsync(_decoded.AsMemory(0, bytes), token).ConfigureAwait(false);
        }

        private int ParseLine()
        {
            var line = _line.AsSpan(0, _length);
            if (line.Length > 0 && line[^1] == '\r') line = line[..^1];
            if (_state == State.AwaitingHeader)
            {
                if (line.StartsWith("begin "u8) && line.Length > 10 && line[9] == ' ' &&
                    IsOctal(line[6]) && IsOctal(line[7]) && IsOctal(line[8])) _state = State.Data;
                return 0;
            }
            if (_state == State.AwaitingEnd)
            {
                if (!line.SequenceEqual("end"u8)) throw new InvalidDataException("The UUencoded block is missing its end line.");
                _state = State.AwaitingHeader;
                _completed = true;
                return 0;
            }
            if (line.Length == 0) throw new InvalidDataException("A UUencoded data line is empty.");
            var count = DecodeCharacter(line[0]);
            if (count > 45 || line.Length != 1 + (count + 2) / 3 * 4)
                throw new InvalidDataException("A UUencoded data line has an invalid length.");
            if (count == 0) _state = State.AwaitingEnd;
            for (int index = 1, destination = 0; destination < count; index += 4)
            {
                var a = DecodeCharacter(line[index]);
                var b = DecodeCharacter(line[index + 1]);
                var c = DecodeCharacter(line[index + 2]);
                var d = DecodeCharacter(line[index + 3]);
                _decoded[destination++] = (byte)(a << 2 | b >> 4);
                if (destination < count) _decoded[destination++] = (byte)(b << 4 | c >> 2);
                if (destination < count) _decoded[destination++] = (byte)(c << 6 | d);
            }
            return count;
        }

        private static bool IsOctal(byte value) => value is >= (byte)'0' and <= (byte)'7';

        private static int DecodeCharacter(byte value) => value switch
        {
            >= (byte)' ' and <= (byte)'`' => (value - ' ') & 63,
            _ => throw new InvalidDataException("A UUencoded data line contains an invalid character.")
        };
    }
}
