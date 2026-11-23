// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Ipfs.Kad;

namespace Nethermind.Ipfs.Ipns;

/// <summary>
///     The "/ipns/" value validator: a DHT value is an IPNS record,
///     and it is stored only when the record verifies on its own
///     terms and names the key it was stored under. The expiry the
///     store evicts on is the record's own validity — a record whose
///     validity has passed is not served, and republishing is what
///     keeps a name alive.
/// </summary>
public sealed class IpnsValueValidator : IDhtValueValidator
{
    public string Namespace => IpnsRecord.Namespace;

    public bool TryValidate(byte[] key, byte[] value, byte[]? heldValue, out DateTimeOffset expires, out string? error)
    {
        expires = default;
        IpnsRecord record;
        try
        {
            record = IpnsRecord.Decode(value);
        }
        catch (Exception ex)
        {
            error = $"IPNS record does not decode: {ex.Message}";
            return false;
        }

        var (ok, reason) = record.Verify(DateTimeOffset.UtcNow);
        if (!ok)
        {
            error = reason;
            return false;
        }

        if (!IpnsRecord.DhtKey(record.Name).AsSpan().SequenceEqual(key))
        {
            error = $"IPNS record name {record.Name} does not match the DHT key it was stored under";
            return false;
        }

        // Replacement policy: ordering is by sequence, never by
        // clock. A record that is not strictly newer than the held
        // one never overwrites it — a stale replay, or a
        // conflicting record at the same sequence, loses to what
        // is held. An equal-sequence duplicate (same value) is
        // accepted; re-putting the current record must stay a
        // no-op, or republishing could never re-announce it.
        if (heldValue is not null)
        {
            IpnsRecord held;
            try
            {
                held = IpnsRecord.Decode(heldValue);
            }
            catch (Exception ex)
            {
                error = $"held IPNS record does not decode: {ex.Message}";
                return false;
            }

            if (record.Sequence < held.Sequence)
            {
                error = $"stale IPNS record: held sequence {held.Sequence}, got {record.Sequence}";
                return false;
            }

            if (record.Sequence == held.Sequence && record.Value != held.Value)
            {
                error = $"conflicting IPNS record: held sequence {held.Sequence} points at {held.Value}, got {record.Value}";
                return false;
            }
        }

        expires = record.Validity;
        error = null;
        return true;
    }
}
