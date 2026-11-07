using System.Text;

namespace DevFoundry.SandboxLauncher;

/// <summary>
/// Docker multiplexes stdout and stderr into one stream of frames: an 8-byte header (stream id,
/// three zero bytes, big-endian length) followed by the payload.
/// </summary>
public static class LogDemuxer
{
    /// <summary>
    /// Reads the frames and keeps the last <paramref name="maxBytesPerStream"/> bytes of each stream:
    /// for a long-running command the end (the result, the error) matters more than the start.
    /// </summary>
    public static async Task<DockerLogs> ReadAsync(Stream stream, int maxBytesPerStream, CancellationToken cancellationToken)
    {
        var stdout = new TailBuffer(maxBytesPerStream);
        var stderr = new TailBuffer(maxBytesPerStream);
        var header = new byte[8];
        var buffer = new byte[8192];

        while (await ReadExactAsync(stream, header, cancellationToken))
        {
            var length = (header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7];
            var target = header[0] == 2 ? stderr : stdout;
            while (length > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, length)), cancellationToken);
                if (read == 0)
                {
                    break;
                }
                length -= read;
                target.Append(buffer, read);
            }
        }
        return new DockerLogs(stdout.Text(), stderr.Text(), stdout.Truncated || stderr.Truncated);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    private sealed class TailBuffer(int capacity)
    {
        private readonly byte[] data = new byte[capacity];
        private int start;
        private int count;

        public bool Truncated { get; private set; }

        public void Append(byte[] source, int length)
        {
            if (capacity == 0)
            {
                Truncated |= length > 0;
                return;
            }
            for (var i = 0; i < length; i++)
            {
                if (count == capacity)
                {
                    data[start] = source[i];
                    start = (start + 1) % capacity;
                    Truncated = true;
                }
                else
                {
                    data[(start + count) % capacity] = source[i];
                    count++;
                }
            }
        }

        public string Text()
        {
            var ordered = new byte[count];
            for (var i = 0; i < count; i++)
            {
                ordered[i] = data[(start + i) % capacity];
            }
            return Encoding.UTF8.GetString(ordered);
        }
    }
}
