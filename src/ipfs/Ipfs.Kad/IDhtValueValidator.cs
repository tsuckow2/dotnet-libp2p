// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Text;

namespace Nethermind.Ipfs.Kad;

/// <summary>
///     What a DHT value must prove before this node stores it, serves
///     it, or announces it: the value is self-authenticating for its
///     key namespace (a signature over the value, a key the name
///     derives from — whatever the namespace's rules are), and it
///     carries its own expiry. The Kad layer is codec-agnostic: it
///     moves opaque bytes and asks the namespace's validator whether
///     they may be kept. A namespace with no validator is rejected
///     outright — an unvalidated value is never stored, served, or
///     announced, and no validator ever trusts the connection that
///     delivered the value (a publisher may announce on the owner's
///     behalf; the value's own proof is the authority).
/// </summary>
public interface IDhtValueValidator
{
    /// <summary>The key namespace this validator speaks, e.g. "/ipns/". Keys are matched by ASCII prefix.</summary>
    string Namespace { get; }

    /// <summary>
    ///     Validates a value for a key in this namespace. Returns false
    ///     with a reason when the value must not be stored or announced.
    ///     <paramref name="expires"/> is the instant the value stops
    ///     being trustworthy — the store evicts it then.
    /// </summary>
    bool TryValidate(byte[] key, byte[] value, out DateTimeOffset expires, out string? error);
}

/// <summary>Key namespaces: the "/xxx/" ASCII prefix of a DHT value key, or null when the key carries none.</summary>
internal static class DhtValueNamespace
{
    public static string? For(byte[] key)
    {
        if (key is not { Length: > 2 } || key[0] != (byte)'/')
        {
            return null;
        }

        int slash = Array.IndexOf(key, (byte)'/', 1);
        if (slash <= 1)
        {
            return null;
        }

        return Encoding.ASCII.GetString(key, 0, slash + 1);
    }
}
