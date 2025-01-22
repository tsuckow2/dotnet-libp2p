// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core.Exceptions;

public class Libp2pException : Exception
{
    public Libp2pException(string? message) : base(message)
    {

    }
    public Libp2pException() : base()
    {

    }
}

public class ChannelClosedException : Libp2pException
{

}


/**
 * Thrown when a protocol could not be successfully negotiated
 */
public class FailedToNegotiateException : Libp2pException
{
    public FailedToNegotiateException(string? message) : base(message)
    {

    }
}
