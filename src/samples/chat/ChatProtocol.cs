// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Text;
using Nethermind.Libp2p.Core;

class ChatProtocolResult
{
    public Task Exit { get; }

    public ChatProtocolResult(Task exit)
    {
        Exit = exit;
    }

}

internal class ChatProtocol : SymmetricProtocol<ChatProtocolResult>
{
    public static ProtocolRef<ChatProtocol, ChatProtocolResult> Ref { get => new(); }

    private static readonly ConsoleReader Reader = new();
    public override string Id => "/chat/1.0.0";


    protected override async Task<IManagedProtocolComm<ChatProtocolResult>> ConnectAsync(IManagedRemoteComm comm, bool isListener)
    {
        var channel = comm.Channel;
        Console.Write("> ");
        _ = Task.Run(async () =>
        {
            for (; ; )
            {
                ReadOnlySequence<byte> read = await channel.ReadAsync(0, ReadBlockingMode.WaitAny).OrThrow();
                Console.Write(Encoding.UTF8.GetString(read).Replace("\n\n", "\n> "));
            }
        });
        var exit = Task.Run(async () =>
        {
            for (; ; )
            {
                string line = await Reader.ReadLineAsync();
                if (line == "exit")
                {
                    return;
                }
                Console.Write("> ");
                byte[] buf = Encoding.UTF8.GetBytes(line + "\n\n");
                await channel.WriteAsync(new ReadOnlySequence<byte>(buf));
            }
        });

        return new ManagedProtocolComm<ChatProtocolResult>(new(exit), comm);
    }
}
