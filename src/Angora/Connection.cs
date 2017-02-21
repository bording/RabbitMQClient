﻿using System;
using System.Binary;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.IO.Pipelines.Networking.Sockets;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

using static Angora.AmqpConstants;

namespace Angora
{
    public class Connection
    {
        static readonly byte[] protocolHeader = { 0x41, 0x4d, 0x51, 0x50, 0x00, 0x00, 0x09, 0x01 };

        static readonly Dictionary<string, object> capabilities = new Dictionary<string, object>
        {
            { "exchange_exchange_bindings", true }
        };

        const ushort connectionChannelNumber = 0;

        readonly string hostName;
        readonly string userName;
        readonly string password;
        readonly string virtualHost;

        readonly Socket socket;

        readonly Dictionary<ushort, Channel> channels;

        readonly TaskCompletionSource<StartResult> startSent = new TaskCompletionSource<StartResult>();
        readonly TaskCompletionSource<bool> openOk = new TaskCompletionSource<bool>();
        readonly TaskCompletionSource<bool> closeOk = new TaskCompletionSource<bool>();
        readonly TaskCompletionSource<bool> readyToOpenConnection = new TaskCompletionSource<bool>();

        ushort nextChannelNumber;
        bool isOpen;

        internal Connection(string hostName, string userName, string password, string virtualHost)
        {
            this.hostName = hostName;
            this.userName = userName;
            this.password = password;
            this.virtualHost = virtualHost;

            socket = new Socket();

            channels = new Dictionary<ushort, Channel>();
        }

        internal async Task Connect(string connectionName = null)
        {
            var addresses = await Dns.GetHostAddressesAsync(hostName);
            var address = addresses.First();
            var endpoint = new IPEndPoint(address, 5672);

            await socket.Connect(endpoint);

            Task.Run(() => ReadLoop()).Ignore();

            var startResult = await Send_ProtocolHeader();
            await Send_StartOk(connectionName);

            await readyToOpenConnection.Task;

            isOpen = await Send_Open();
        }

        public async Task<Channel> CreateChannel()
        {
            var channel = new Channel(socket, ++nextChannelNumber);
            channels.Add(channel.ChannelNumber, channel);

            await channel.Open();

            return channel;
        }

        public Task Close()
        {
            return Close(true);
        }

        async Task Close(bool client)
        {
            if (isOpen)
            {
                isOpen = false;

                if (client)
                {
                    await Send_Close();
                }
                else
                {
                    await Send_CloseOk();
                }

                socket.Close();
            }
            else
            {
                throw new Exception("already closed");
            }
        }

        async Task ReadLoop()
        {
            while (true)
            {
                var readResult = await socket.Input.ReadAsync();
                var buffer = readResult.Buffer;

                if (buffer.IsEmpty && readResult.IsCompleted)
                {
                    break;
                }

                var frameType = buffer.ReadBigEndian<byte>();
                buffer = buffer.Slice(sizeof(byte));

                var channelNumber = buffer.ReadBigEndian<ushort>();
                buffer = buffer.Slice(sizeof(ushort));

                var payloadSize = buffer.ReadBigEndian<uint>();
                buffer = buffer.Slice(sizeof(uint));

                var payload = buffer.Slice(buffer.Start, (int)payloadSize);
                buffer = buffer.Slice((int)payloadSize);

                var frameEnd = buffer.ReadBigEndian<byte>();
                buffer = buffer.Slice(sizeof(byte));

                if (frameEnd != FrameEnd)
                {
                    //TODO other stuff here around what this means
                    throw new Exception();
                }

                switch (frameType)
                {
                    case FrameType.Method:
                        await HandleIncomingMethodFrame(channelNumber, payload);
                        break;
                }

                socket.Input.Advance(buffer.Start, buffer.End);
            }
        }

        async Task SendHeartbeats(ushort interval)
        {
            await Task.Delay(200);

            while (true)
            {
                var buffer = await socket.GetWriteBuffer();

                try
                {
                    if (socket.HeartbeatNeeded)
                    {
                        uint length = 0;

                        buffer.WriteBigEndian(FrameType.Heartbeat);
                        buffer.WriteBigEndian(connectionChannelNumber);
                        buffer.WriteBigEndian(length);
                        buffer.WriteBigEndian(FrameEnd);
                    }

                    await buffer.FlushAsync();
                }
                finally
                {
                    socket.ReleaseWriteBuffer(true);
                }

                await Task.Delay(TimeSpan.FromSeconds(interval));
            }
        }

        Task HandleIncomingMethodFrame(ushort channelNumber, ReadableBuffer payload)
        {
            var method = payload.ReadBigEndian<uint>();
            payload = payload.Slice(sizeof(uint));

            var classId = method >> 16;

            if (classId == ClassId.Connection) //TODO validate channel 0
            {
                return HandleIncomingMethod(method, payload);
            }
            else
            {
                channels[channelNumber].HandleIncomingMethod(method, payload);
            }

            return Task.CompletedTask;
        }

        async Task HandleIncomingMethod(uint method, ReadableBuffer arguments)
        {
            switch (method)
            {
                case Method.Connection.Start:
                    Handle_Start(arguments);
                    break;

                case Method.Connection.Tune:
                    await Handle_Tune(arguments);
                    break;

                case Method.Connection.OpenOk:
                    Handle_OpenOk();
                    break;

                case Method.Connection.CloseOk:
                    Handle_CloseOk();
                    break;

                case Method.Connection.Close:
                    await Handle_Close(arguments);
                    break;
            }
        }

        struct StartResult
        {
            public byte VersionMajor;
            public byte VersionMinor;
            public Dictionary<string, object> ServerProperties;
            public string Mechanisms;
            public string Locales;
        }

        void Handle_Start(ReadableBuffer arguments)
        {
            StartResult result;
            ReadCursor cursor;

            result.VersionMajor = arguments.ReadBigEndian<byte>();
            arguments = arguments.Slice(sizeof(byte));

            result.VersionMinor = arguments.ReadBigEndian<byte>();
            arguments = arguments.Slice(sizeof(byte));

            (result.ServerProperties, cursor) = arguments.ReadTable();
            arguments = arguments.Slice(cursor);

            (result.Mechanisms, cursor) = arguments.ReadLongString();
            arguments = arguments.Slice(cursor);

            (result.Locales, cursor) = arguments.ReadLongString();

            startSent.SetResult(result);
        }

        async Task Handle_Tune(ReadableBuffer arguments)
        {
            var channelMax = arguments.ReadBigEndian<ushort>();
            arguments = arguments.Slice(sizeof(ushort));

            var frameMax = arguments.ReadBigEndian<uint>();
            arguments = arguments.Slice(sizeof(uint));

            var heartbeat = arguments.ReadBigEndian<ushort>();

            Task.Run(() => SendHeartbeats(heartbeat)).Ignore();

            await Send_TuneOk(channelMax, frameMax, heartbeat);
        }

        void Handle_OpenOk()
        {
            openOk.SetResult(true);
        }

        void Handle_CloseOk()
        {
            closeOk.SetResult(true);
        }

        async Task Handle_Close(ReadableBuffer arguments)
        {
            var replyCode = arguments.ReadBigEndian<ushort>();
            arguments = arguments.Slice(sizeof(ushort));

            var (replyText, cursor) = arguments.ReadShortString();
            arguments = arguments.Slice(cursor);

            var method = arguments.ReadBigEndian<uint>();

            await Close(false);

            foreach (var channel in channels)
            {
                channel.Value.Handle_Connection_Close(replyCode, replyText, method);
            }
        }

        async Task<StartResult> Send_ProtocolHeader()
        {
            var buffer = await socket.GetWriteBuffer();

            try
            {
                buffer.Write(protocolHeader);

                await buffer.FlushAsync();
            }
            finally
            {
                socket.ReleaseWriteBuffer();
            }

            return await startSent.Task;
        }

        async Task Send_StartOk(string connectionName)
        {
            var buffer = await socket.GetWriteBuffer();

            try
            {
                var payloadSizeHeader = buffer.WriteFrameHeader(FrameType.Method, connectionChannelNumber);

                buffer.WriteBigEndian(Method.Connection.StartOk);

                var clientProperties = new Dictionary<string, object>
                {
                    { "product", "Angora" },
                    { "capabilities", capabilities },
                    { "connection_name", connectionName }
                };

                buffer.WriteTable(clientProperties);
                buffer.WriteShortString("PLAIN"); //mechanism
                buffer.WriteLongString($"\0{userName}\0{password}"); //response
                buffer.WriteShortString("en_US"); //locale

                payloadSizeHeader.WriteBigEndian((uint)buffer.BytesWritten - FrameHeaderSize);

                buffer.WriteBigEndian(FrameEnd);

                await buffer.FlushAsync();
            }
            finally
            {
                socket.ReleaseWriteBuffer();
            }
        }

        async Task Send_TuneOk(ushort channelMax, uint frameMax, ushort heartbeat)
        {
            var buffer = await socket.GetWriteBuffer();

            try
            {
                var payloadSizeHeader = buffer.WriteFrameHeader(FrameType.Method, connectionChannelNumber);

                buffer.WriteBigEndian(Method.Connection.TuneOk);
                buffer.WriteBigEndian(channelMax);
                buffer.WriteBigEndian(frameMax);
                buffer.WriteBigEndian(heartbeat);

                payloadSizeHeader.WriteBigEndian((uint)buffer.BytesWritten - FrameHeaderSize);

                buffer.WriteBigEndian(FrameEnd);

                await buffer.FlushAsync();
            }
            finally
            {
                socket.ReleaseWriteBuffer();
            }

            readyToOpenConnection.SetResult(true);
        }

        async Task<bool> Send_Open()
        {
            var buffer = await socket.GetWriteBuffer();

            try
            {
                var payloadSizeHeader = buffer.WriteFrameHeader(FrameType.Method, connectionChannelNumber);

                buffer.WriteBigEndian(Method.Connection.Open);
                buffer.WriteShortString(virtualHost);
                buffer.WriteBigEndian(Reserved);
                buffer.WriteBigEndian(Reserved);

                payloadSizeHeader.WriteBigEndian((uint)buffer.BytesWritten - FrameHeaderSize);

                buffer.WriteBigEndian(FrameEnd);

                await buffer.FlushAsync();
            }
            finally
            {
                socket.ReleaseWriteBuffer();
            }

            return await openOk.Task;
        }

        async Task Send_Close(ushort replyCode = ConnectionReplyCode.Success, string replyText = "Goodbye", ushort failingClass = 0, ushort failingMethod = 0)
        {
            var buffer = await socket.GetWriteBuffer();

            try
            {
                var payloadSizeHeader = buffer.WriteFrameHeader(FrameType.Method, connectionChannelNumber);

                buffer.WriteBigEndian(Method.Connection.Close);
                buffer.WriteBigEndian(replyCode);
                buffer.WriteShortString(replyText);
                buffer.WriteBigEndian(failingClass);
                buffer.WriteBigEndian(failingMethod);

                payloadSizeHeader.WriteBigEndian((uint)buffer.BytesWritten - FrameHeaderSize);

                buffer.WriteBigEndian(FrameEnd);

                await buffer.FlushAsync();
            }
            finally
            {
                socket.ReleaseWriteBuffer();
            }

            await closeOk.Task;
        }

        async Task Send_CloseOk()
        {
            var buffer = await socket.GetWriteBuffer();

            try
            {
                var payloadSizeHeader = buffer.WriteFrameHeader(FrameType.Method, connectionChannelNumber);

                buffer.WriteBigEndian(Method.Connection.CloseOk);

                payloadSizeHeader.WriteBigEndian((uint)buffer.BytesWritten - FrameHeaderSize);

                buffer.WriteBigEndian(FrameEnd);

                await buffer.FlushAsync();
            }
            finally
            {
                socket.ReleaseWriteBuffer();
            }
        }
    }
}
