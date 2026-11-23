// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
/// The inbound identity proof for the quiche transport's stack path.
/// quiche's C API gives the listener no client certificate (a server
/// with verification off never requests one, and verification on is
/// unusable — see the 2026-10-28 cert experiment), so inbound
/// identity cannot come from the TLS handshake the way it does for
/// msquic. Instead the dialer sends one dedicated first stream
/// carrying this frame, and the listener validates it before handing
/// any stream up:
///   magic "/quiche-proof/1.0.0\n", then big-endian-length-prefixed
///   dialer PeerId bytes, dialer public-key protobuf bytes, an
///   8-byte big-endian unix timestamp, listener PeerId bytes, and
///   the dialer's signature over (prefix + listener + dialer +
///   timestamp + public key).
/// The frame is sound because it only ever travels inside the
/// TLS-encrypted direct connection to the listener the dialer just
/// authenticated via the dial-side certificate proof: an attacker
/// cannot observe it, cannot forge the dialer's signature, and
/// cannot replay it to another listener (bound to the listener's
/// PeerId) or later (bound to a 120 s window). A dialer that sends
/// no proof frame (any other QUIC implementation) is handed up
/// unproven instead — the listener peeks at the first stream's first
/// bytes and replays them when they are not the magic — so this
/// costs no interop.
/// </summary>
public static class QuicheProofFrame
{
    public static readonly byte[] Magic = "/quiche-proof/1.0.0\n"u8.ToArray();

    private static readonly byte[] SignaturePrefix = "libp2p-quiche-proof:"u8.ToArray();

    /// <summary>How old a proof frame may be before it is stale.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(120);

    public static byte[] Encode(Identity dialer, PeerId listener)
    {
        byte[] dialerPeerId = dialer.PeerId.Bytes;
        byte[] keyBytes = dialer.PublicKey.ToByteArray();
        byte[] listenerPeerId = listener.Bytes;
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        byte[] timestampBytes = new byte[8];
        for (int i = 0; i < 8; i++)
        {
            timestampBytes[7 - i] = (byte)(timestamp >> (8 * i));
        }
        byte[] content = SignatureContent(listenerPeerId, dialerPeerId, timestampBytes, keyBytes);
        byte[] signature = dialer.Sign(content);

        using var stream = new MemoryStream();
        stream.Write(Magic);
        WriteLengthPrefixed(stream, dialerPeerId);
        WriteLengthPrefixed(stream, keyBytes);
        stream.Write(timestampBytes);
        WriteLengthPrefixed(stream, listenerPeerId);
        WriteLengthPrefixed(stream, signature);
        return stream.ToArray();
    }

    /// <summary>
    /// Validates a proof frame against this listener's PeerId.
    /// Returns the proven dialer identity, or null when the frame
    /// is malformed, stale, bound to another listener, or signed by
    /// a key that does not match the dialer PeerId it names.
    /// </summary>
    public static Identity? Validate(byte[] frame, PeerId ownPeerId)
    {
        try
        {
            if (frame.Length < Magic.Length || !frame.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                return null;
            }
            int offset = Magic.Length;
            byte[] dialerPeerId = ReadLengthPrefixed(frame, ref offset);
            byte[] keyBytes = ReadLengthPrefixed(frame, ref offset);
            if (offset + 8 > frame.Length)
            {
                return null;
            }
            byte[] timestampBytes = frame.AsSpan(offset, 8).ToArray();
            offset += 8;
            byte[] listenerPeerId = ReadLengthPrefixed(frame, ref offset);
            byte[] signature = ReadLengthPrefixed(frame, ref offset);
            if (offset != frame.Length)
            {
                return null;
            }

            long timestamp = 0;
            for (int i = 0; i < 8; i++)
            {
                timestamp = (timestamp << 8) | timestampBytes[i];
            }
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(now - timestamp) > (long)MaxAge.TotalSeconds)
            {
                return null;
            }
            if (!listenerPeerId.AsSpan().SequenceEqual(ownPeerId.Bytes))
            {
                return null;
            }

            PublicKey key = PublicKey.Parser.ParseFrom(keyBytes);
            var identity = new Identity(key);
            if (!identity.PeerId.Bytes.AsSpan().SequenceEqual(dialerPeerId))
            {
                return null;
            }
            byte[] content = SignatureContent(listenerPeerId, dialerPeerId, timestampBytes, keyBytes);
            return identity.VerifySignature(content, signature) ? identity : null;
        }
        catch
        {
            // Malformed frames (short reads, bad protobuf) are just
            // invalid proofs, never exceptions to the accept loop.
            return null;
        }
    }

    private static byte[] SignatureContent(byte[] listenerPeerId, byte[] dialerPeerId, byte[] timestampBytes, byte[] keyBytes)
    {
        using var stream = new MemoryStream();
        stream.Write(SignaturePrefix);
        stream.Write(listenerPeerId);
        stream.Write(dialerPeerId);
        stream.Write(timestampBytes);
        stream.Write(keyBytes);
        return stream.ToArray();
    }

    private static void WriteLengthPrefixed(Stream stream, byte[] bytes)
    {
        byte[] length = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            length[3 - i] = (byte)(bytes.Length >> (8 * i));
        }
        stream.Write(length);
        stream.Write(bytes);
    }

    private static byte[] ReadLengthPrefixed(byte[] frame, ref int offset)
    {
        if (offset + 4 > frame.Length)
        {
            throw new InvalidDataException("Proof frame truncated in a length prefix.");
        }
        int length = 0;
        for (int i = 0; i < 4; i++)
        {
            length = (length << 8) | frame[offset + i];
        }
        offset += 4;
        if (length < 0 || offset + length > frame.Length)
        {
            throw new InvalidDataException("Proof frame truncated in a field.");
        }
        byte[] bytes = frame.AsSpan(offset, length).ToArray();
        offset += length;
        return bytes;
    }
}
