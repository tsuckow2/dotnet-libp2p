// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Multiformats.Address.Protocols;

namespace Nethermind.Libp2p.Core;

public static class MultiaddressExtensions
{
    /// <summary>
    ///     The /p2p/ component an address terminates at — the peer the
    ///     address ultimately names. A routed address carries an
    ///     earlier /p2p/ for the relay it passes through
    ///     (/ip4/…/p2p/&lt;relay&gt;/p2p-circuit/p2p/&lt;destination&gt;);
    ///     the peer being addressed is always the final one. (The
    ///     vendored <c>Get&lt;P2P&gt;()</c> uses SingleOrDefault and
    ///     throws on routed addresses.)
    /// </summary>
    public static P2P? TerminalP2P(this Multiaddress address)
    {
        return address.Protocols.OfType<P2P>().LastOrDefault();
    }
}
