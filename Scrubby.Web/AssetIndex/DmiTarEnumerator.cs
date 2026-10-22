using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Scrubby.Web.AssetIndex;

public static class DmiTarEnumerator
{
    private const int BlockSize = 512;

    public static async Task EnumerateDmiAsync(
        Stream tar,
        Func<string, byte[], Task> onDmi,
        CancellationToken cancellationToken)
    {
        var header = new byte[BlockSize];
        var scratch = new byte[BlockSize];
        string pendingLongName = null;
        string pendingPaxPath = null;

        while (true)
        {
            if (!await TryReadBytesAsync(tar, header, BlockSize, cancellationToken)) return;
            if (IsZeroBlock(header))
            {
                if (!await TryReadBytesAsync(tar, header, BlockSize, cancellationToken) || IsZeroBlock(header))
                    return;
            }

            var size = ParseNum(header, 124, 12);
            var typeflag = header[156];
            var roundedSize = ((size + BlockSize - 1) / BlockSize) * BlockSize;
            var padding = roundedSize - size;

            switch (typeflag)
            {
                case (byte)'L':
                    pendingLongName = await ReadDataAsStringAsync(tar, size, cancellationToken);
                    await SkipAsync(tar, padding, scratch, cancellationToken);
                    continue;
                case (byte)'K':
                    await SkipAsync(tar, roundedSize, scratch, cancellationToken);
                    continue;
                case (byte)'x':
                case (byte)'g':
                case (byte)'X':
                    pendingPaxPath = await ReadPaxPathAsync(tar, size, cancellationToken);
                    await SkipAsync(tar, padding, scratch, cancellationToken);
                    continue;
            }

            var name = pendingLongName ?? ReadString(header, 0, 100);
            name = pendingPaxPath ?? name;
            pendingLongName = null;
            pendingPaxPath = null;

            cancellationToken.ThrowIfCancellationRequested();

            if (typeflag is (byte)'0' or 0 or (byte)'7')
            {
                if (size <= 0 || !name.EndsWith(".dmi", StringComparison.OrdinalIgnoreCase))
                {
                    await SkipAsync(tar, roundedSize, scratch, cancellationToken);
                    continue;
                }

                var data = new byte[size];
                if (!await TryReadBytesAsync(tar, data, (int)size, cancellationToken)) return;
                await SkipAsync(tar, padding, scratch, cancellationToken);
                await onDmi(name, data);
            }
            else if (typeflag is (byte)'5' or (byte)'1' or (byte)'2' or (byte)'3'
                     or (byte)'4' or (byte)'6' or (byte)'D')
            {
                // directory or symlink/hardlink/device metadata: no data blocks
            }
            else
            {
                await SkipAsync(tar, roundedSize, scratch, cancellationToken);
            }
        }
    }

    private static async Task<string> ReadDataAsStringAsync(Stream tar, long size, CancellationToken ct)
    {
        if (size <= 0 || size > 1 << 20) return null;
        var payload = new byte[size];
        if (!await TryReadBytesAsync(tar, payload, (int)size, ct)) return null;
        return ReadAscii(payload);
    }

    private static async Task<string> ReadPaxPathAsync(Stream tar, long size, CancellationToken ct)
    {
        if (size <= 0 || size > 1 << 20)
        {
            SkipSync(tar, size);
            return null;
        }
        var payload = new byte[size];
        if (!await TryReadBytesAsync(tar, payload, (int)size, ct)) return null;
        var text = System.Text.Encoding.ASCII.GetString(payload);

        var pos = 0;
        while (pos < text.Length && text[pos] >= '0' && text[pos] <= '9')
        {
            var space = text.IndexOf(' ', pos);
            if (space < pos) break;
            if (!long.TryParse(text.AsSpan(pos, space - pos), out var recordLength) || recordLength <= 0) break;
            var end = Math.Min(pos + (int)recordLength, text.Length);
            var eq = text.IndexOf('=', space + 1, end - space - 1);
            if (eq > space)
            {
                var key = text.Substring(space + 1, eq - space - 1);
                if (key == "path") return text.Substring(eq + 1, end - eq - 1);
            }
            pos = end;
        }
        return null;
    }

    private static void SkipSync(Stream tar, long count)
    {
        while (count > 0)
        {
            var read = tar.Read(new byte[81920], 0, (int)Math.Min(count, 81920));
            if (read <= 0) return;
            count -= read;
        }
    }

    private static async Task<bool> TryReadBytesAsync(Stream tar, byte[] buffer, int count, CancellationToken ct)
    {
        var total = 0;
        while (total < count)
        {
            var read = await tar.ReadAsync(buffer.AsMemory(total, count - total), ct);
            if (read <= 0) return total == count;
            total += read;
        }
        return true;
    }

    private static async Task SkipAsync(Stream tar, long count, byte[] scratch, CancellationToken ct)
    {
        if (count <= 0) return;
        while (count > 0)
        {
            var step = (int)Math.Min(count, scratch.Length);
            if (!await TryReadBytesAsync(tar, scratch, step, ct)) return;
            count -= step;
        }
    }

    private static bool IsZeroBlock(byte[] block)
    {
        foreach (var b in block)
        {
            if (b != 0) return false;
        }
        return true;
    }

    private static string ReadString(byte[] data, int offset, int length)
    {
        var end = 0;
        while (end < length && data[offset + end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(data, offset, end).TrimEnd();
    }

    private static string ReadAscii(byte[] payload)
    {
        var end = payload.Length;
        while (end > 0 && payload[end - 1] == 0) end--;
        var text = System.Text.Encoding.ASCII.GetString(payload, 0, end).TrimEnd();
        return text;
    }

    private static long ParseNum(byte[] data, int offset, int length)
    {
        if ((data[offset] & 0x80) != 0)
        {
            long value = 0;
            for (var i = 1; i < length; i++) value = (value << 8) | (uint)data[offset + i];
            return value;
        }

        var pos = 0;
        while (pos < length && (data[offset + pos] == ' ' || data[offset + pos] == 0)) pos++;
        long result = 0;
        while (pos < length)
        {
            var c = data[offset + pos];
            if (c < '0' || c > '7') break;
            result = result * 8 + (c - '0');
            pos++;
        }
        return result;
    }
}
