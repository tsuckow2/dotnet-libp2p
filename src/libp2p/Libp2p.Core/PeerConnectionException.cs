// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:MIT

namespace Nethermind.Libp2p.Core;

public class PeerConnectionException : Exception
{
    public PeerConnectionException()
    {
    }

    public PeerConnectionException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
