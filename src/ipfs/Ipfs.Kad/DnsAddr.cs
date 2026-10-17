// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nethermind.Ipfs.Kad;

/// <summary>
///     /dnsaddr/ resolution (https://github.com/multiformats/multiaddr
///     /protocols/DNSADDR.md): a host publishes its multiaddresses as
///     DNS TXT records at _dnsaddr.&lt;host&gt;, each of the form
///     "dnsaddr=/ip4/.../p2p/...". An entry may itself be another
///     /dnsaddr/ name, resolved recursively. This is how the IPFS
///     network's maintained bootstrap set is published — the classic
///     IP literals have mostly retired.
/// </summary>
internal static class DnsAddr
{
    private const int MaxDepth = 4;

    /// <summary>Expands one bootstrap address, resolving any /dnsaddr/ part into concrete addresses.</summary>
    public static async Task<IReadOnlyList<string>> ExpandAsync(string address, CancellationToken token = default)
    {
        var results = new List<string>();
        await ExpandAsync(address, 0, results, token);
        return results;
    }

    private static async Task ExpandAsync(string address, int depth, List<string> results, CancellationToken token)
    {
        string? host = DnsAddrHost(address);
        if (host is null)
        {
            results.Add(address);
            return;
        }

        if (depth >= MaxDepth)
        {
            return;
        }

        foreach (string record in await QueryTxtAsync("_dnsaddr." + host, token))
        {
            if (!record.StartsWith("dnsaddr="))
            {
                continue;
            }

            string resolved = record["dnsaddr=".Length..];
            // The record replaces the /dnsaddr/<host> prefix of the
            // original address; anything after it (a /p2p/ part, in
            // practice) carries over.
            string suffix = address[(address.IndexOf(host, StringComparison.Ordinal) + host.Length)..];
            await ExpandAsync(resolved + suffix, depth + 1, results, token);
        }
    }

    /// <summary>The host of a /dnsaddr/&lt;host&gt; address, or null when the address has none.</summary>
    internal static string? DnsAddrHost(string address)
    {
        const string marker = "/dnsaddr/";
        int start = address.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        int end = address.IndexOf('/', start);
        return end < 0 ? address[start..] : address[start..end];
    }

    /// <summary>Reads the system resolvers from /etc/resolv.conf, falling back to public ones.</summary>
    internal static IReadOnlyList<IPAddress> SystemResolvers()
    {
        var resolvers = new List<IPAddress>();
        try
        {
            foreach (string line in File.ReadLines("/etc/resolv.conf"))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0] == "nameserver" && IPAddress.TryParse(parts[1], out IPAddress? ip))
                {
                    resolvers.Add(ip);
                }
            }
        }
        catch (IOException)
        {
        }

        if (resolvers.Count == 0)
        {
            resolvers.Add(IPAddress.Parse("1.1.1.1"));
            resolvers.Add(IPAddress.Parse("8.8.8.8"));
        }

        return resolvers;
    }

    private static async Task<IReadOnlyList<string>> QueryTxtAsync(string name, CancellationToken token)
    {
        byte[] query = BuildQuery(name);
        foreach (IPAddress resolver in SystemResolvers())
        {
            using var udp = new UdpClient(resolver.AddressFamily);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await udp.SendAsync(query, new IPEndPoint(resolver, 53), timeout.Token);
                UdpReceiveResult received = await udp.ReceiveAsync(timeout.Token);
                return ParseTxtRecords(received.Buffer);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                // Try the next resolver.
            }
        }

        return [];
    }

    internal static byte[] BuildQuery(string name, ushort id = 0x2a41)
    {
        using var stream = new MemoryStream();
        WriteU16(stream, id);
        WriteU16(stream, 0x0100); // RD
        WriteU16(stream, 1);      // QDCOUNT
        WriteU16(stream, 0);
        WriteU16(stream, 0);
        WriteU16(stream, 0);
        foreach (string label in name.Split('.'))
        {
            stream.WriteByte((byte)label.Length);
            stream.Write(Encoding.ASCII.GetBytes(label));
        }

        stream.WriteByte(0);
        WriteU16(stream, 16); // TXT
        WriteU16(stream, 1);  // IN
        return stream.ToArray();
    }

    /// <summary>Extracts the TXT strings from a DNS response packet.</summary>
    internal static IReadOnlyList<string> ParseTxtRecords(byte[] packet)
    {
        var records = new List<string>();
        if (packet.Length < 12)
        {
            return records;
        }

        int questions = ReadU16(packet, 4);
        int answers = ReadU16(packet, 6);
        int offset = 12;
        for (int i = 0; i < questions; i++)
        {
            offset = SkipName(packet, offset) + 4;
        }

        for (int i = 0; i < answers && offset < packet.Length; i++)
        {
            offset = SkipName(packet, offset);
            if (offset + 10 > packet.Length)
            {
                break;
            }

            int type = ReadU16(packet, offset);
            int length = ReadU16(packet, offset + 8);
            offset += 10;
            if (type == 16) // TXT: a sequence of length-prefixed strings, concatenated per record
            {
                var text = new StringBuilder();
                int end = Math.Min(offset + length, packet.Length);
                while (offset < end)
                {
                    int chunk = packet[offset++];
                    text.Append(Encoding.UTF8.GetString(packet, offset, Math.Min(chunk, end - offset)));
                    offset += chunk;
                }

                records.Add(text.ToString());
            }
            else
            {
                offset += length;
            }
        }

        return records;
    }

    private static int SkipName(byte[] packet, int offset)
    {
        while (offset < packet.Length)
        {
            int length = packet[offset];
            if (length == 0)
            {
                return offset + 1;
            }

            if ((length & 0xC0) == 0xC0)
            {
                return offset + 2; // compression pointer
            }

            offset += 1 + length;
        }

        return offset;
    }

    private static int ReadU16(byte[] bytes, int offset) => (bytes[offset] << 8) | bytes[offset + 1];

    private static void WriteU16(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}
