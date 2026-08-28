// Copyright (c) Gothos
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

//#define SERVER

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Data;
using Tera;
using Tera.Game;
using Tera.Sniffing;

namespace DamageMeter.Sniffing
{
    public class BaseSniffer : ITeraSniffer
    {
        public event Action<Message> MessageReceived;
        public event Action<Server> NewConnection;
        public event Action EndConnection;

        public virtual bool Enabled { get; set; }

        public ConcurrentQueue<Message> Packets { get; private set; } =
            new ConcurrentQueue<Message>();
        public virtual bool Connected { get; set; }

        public void ClearPackets()
        {
            Packets = new ConcurrentQueue<Message>();
        }

        public Queue<Message> GetPacketsLogsAndStop()
        {
            var tmp = PacketsCopyStorage ?? new Queue<Message>();
            EnableMessageStorage = false;
            // Wait for thread to sync, more perf than concurrentQueue
            Thread.Sleep(1);
            return tmp;
        }

        public event Action<string> Warning;

        public virtual void CleanupForcefully()
        {
            Connected = false;
            OnEndConnection();
        }

        private Queue<Message> PacketsCopyStorage;

        private bool _enableMessageStorage;
        public bool EnableMessageStorage
        {
            get => _enableMessageStorage;
            set
            {
                _enableMessageStorage = value;
                if (!_enableMessageStorage)
                {
                    PacketsCopyStorage = null;
                }
            }
        }

        protected virtual void OnNewConnection(Server server)
        {
            PacketsCopyStorage = EnableMessageStorage ? new Queue<Message>() : null;
            NewConnection?.Invoke(server);
        }

        protected virtual void OnMessageReceived(Message message)
        {
            Packets.Enqueue(message);
            PacketsCopyStorage?.Enqueue(message);
        }

        protected virtual void OnEndConnection()
        {
            EndConnection?.Invoke();
        }

        protected virtual void OnWarning(string obj)
        {
            Warning?.Invoke(obj);
        }
    }

    public class TeraSniffer : BaseSniffer
    {
        private ConnectionDecrypter _decrypter;
        private MessageSplitter _messageSplitter;

        private bool _enabled;
        private readonly string _socketHost;
        private readonly int _socketPort;
        private readonly bool _scanLocalMirrorPorts;
        private const int MirrorConnectTimeoutMs = 30;
        private const int PreferredMirrorProbeInterval = 16;
        private CancellationTokenSource _socketCts;
        private Task _socketTask;

        private bool _connected;

        public override bool Connected
        {
            get => _connected;
            set
            {
                if (_connected == value)
                    return;
                _connected = value;
                if (!_connected)
                    OnEndConnection();
            }
        }

        public override bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                    return;
                _enabled = value;

                if (_enabled)
                {
                    if (_socketTask == null || _socketTask.IsCompleted)
                    {
                        _socketCts = new CancellationTokenSource();
                        _socketTask = Task.Run(() =>
                            UnencryptedSocketLoopAsync(_socketCts.Token)
                        );
                    }
                }
                else
                    _socketCts?.Cancel();
            }
        }

        public TeraSniffer()
        {
            _socketHost = "127.0.0.1";
            _socketPort = 7803;
            _scanLocalMirrorPorts = true;
        }

        public TeraSniffer(string socketHost, int socketPort)
        {
            _socketHost = socketHost;
            _socketPort = socketPort;
            _scanLocalMirrorPorts = false;
        }

        private async Task<TcpClient> TryConnectToMirrorPortAsync(int port, CancellationToken token)
        {
            var candidate = new TcpClient();
            var connectTask = candidate.ConnectAsync(_socketHost, port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(MirrorConnectTimeoutMs, token));
            if (completed != connectTask)
            {
                candidate.Close();
                token.ThrowIfCancellationRequested();
                try { await connectTask; } catch { }
                return null;
            }

            try
            {
                await connectTask;
                return candidate;
            }
            catch (SocketException)
            {
                candidate.Close();
                return null;
            }
        }

        private async Task<TcpClient> ConnectToMirrorAsync(CancellationToken token)
        {
            if (!_scanLocalMirrorPorts)
            {
                var configuredClient = new TcpClient();
                await configuredClient.ConnectAsync(_socketHost, _socketPort);
                return configuredClient;
            }

            // Noctenium normally binds 7803 shortly after TERA starts. The old
            // unbounded sequential scan could spend minutes waiting on reserved
            // Windows ports before trying 7803 again, forcing Shinra to depend on
            // a reconstructed handshake backlog. Keep probing the preferred port
            // during the bounded fallback scan so attachment happens promptly.
            for (var port = 7804; port <= 8002; port++)
            {
                if ((port - 7804) % PreferredMirrorProbeInterval == 0)
                {
                    var preferred = await TryConnectToMirrorPortAsync(7803, token);
                    if (preferred != null) { return preferred; }
                }

                var candidate = await TryConnectToMirrorPortAsync(port, token);
                if (candidate != null) { return candidate; }
            }

            throw new SocketException((int)SocketError.ConnectionRefused);
        }

        public override void CleanupForcefully()
        {
            try
            {
                _socketCts?.Cancel();
            }
            catch { }
            base.CleanupForcefully();
        }

        private void OnResync(MessageDirection direction, int skipped, int size)
        {
            BasicTeraData.LogError(
                "Resync occured " + direction + ", skipped:" + skipped + ", block size:" + size,
                false,
                true
            );
        }

        private void HandleMessageReceived(Message message)
        {
            OnMessageReceived(message);
        }

        private void HandleServerToClientDecrypted(byte[] data)
        {
            _messageSplitter.ServerToClient(DateTime.UtcNow, data);
        }

        private void HandleClientToServerDecrypted(byte[] data)
        {
            _messageSplitter.ClientToServer(DateTime.UtcNow, data);
        }

        // Unencrypted socket mode: connect to the local mirror and feed framed ciphertext.
        private async Task UnencryptedSocketLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client = null;
                try
                {
                    client = await ConnectToMirrorAsync(token);
                    Connected = true;
                    var stream = client.GetStream();

                    // Public Shinra only knows the existing mirror contract: EUC region,
                    // decrypted locally from framed bytes coming from the mirror.
                    var server = new Server("Classic+", "EUC", _socketHost);
                    _decrypter = new ConnectionDecrypter(server.Region);
                    _decrypter.ClientToServerDecrypted += HandleClientToServerDecrypted;
                    _decrypter.ServerToClientDecrypted += HandleServerToClientDecrypted;

                    _messageSplitter = new MessageSplitter();
                    _messageSplitter.MessageReceived += HandleMessageReceived;
                    _messageSplitter.Resync += OnResync;
                    OnNewConnection(server);

                    var lenBuf = new byte[2];
                    while (!token.IsCancellationRequested)
                    {
                        if (!ReadExact(stream, lenBuf, 2))
                            break;

                        var totalLen = BitConverter.ToUInt16(lenBuf, 0);
                        if (totalLen < 1)
                            continue;

                        var dirBuf = new byte[1];
                        if (!ReadExact(stream, dirBuf, 1))
                            break;

                        var direction = dirBuf[0];
                        var payloadLen = totalLen - 1;
                        var payload = new byte[payloadLen];
                        if (payloadLen > 0 && !ReadExact(stream, payload, payloadLen))
                            break;

                        if (direction == 1)
                        {
                            _decrypter.ClientToServer(payload, 0);
                        }
                        else if (direction == 2)
                        {
                            _decrypter.ServerToClient(payload, 0);
                        }
                        else
                        {
                            OnWarning($"[Unencrypted] Unknown direction byte={direction}, skipping frame of totalLen={totalLen}");
                            continue;
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    try
                    {
                        client?.Close();
                    }
                    catch { }
                    if (Connected)
                    {
                        Connected = false;
                        OnEndConnection();
                    }
                }
                if (!token.IsCancellationRequested)
                {
                    await Task.Delay(2000, token).ContinueWith(_ => { });
                }
            }
        }

        private static bool ReadExact(NetworkStream stream, byte[] buffer, int length)
        {
            int progress = 0;
            while (progress < length)
            {
                var read = 0;
                try
                {
                    read = stream.Read(buffer, progress, length - progress);
                }
                catch
                {
                    return false;
                }
                if (read <= 0)
                    return false;
                progress += read;
            }
            return true;
        }
    }
}
