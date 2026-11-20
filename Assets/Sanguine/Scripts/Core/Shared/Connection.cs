using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace Sanguine
{
    /// <summary>
    /// A framed TCP connection: every message is a 4-byte little-endian length
    /// followed by the payload. Reads and writes happen on background threads;
    /// the game thread polls <see cref="TryReceive"/> and calls <see cref="Send"/>.
    /// Used by the server (one per client) and by the client.
    /// </summary>
    public sealed class Connection
    {
        public const int MaxFrame = 256 * 1024;

        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly ConcurrentQueue<byte[]> _incoming = new ConcurrentQueue<byte[]>();
        private readonly BlockingCollection<byte[]> _outgoing = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), 4096);
        private volatile bool _closed;
        private string _closeReason = "";

        public readonly string RemoteAddress;

        public bool IsClosed { get { return _closed; } }
        public string CloseReason { get { return _closeReason; } }

        public Connection(TcpClient tcp)
        {
            _tcp = tcp;
            _tcp.NoDelay = true;
            _stream = tcp.GetStream();
            try { RemoteAddress = tcp.Client.RemoteEndPoint.ToString(); } catch { RemoteAddress = "?"; }

            var reader = new Thread(ReadLoop) { IsBackground = true, Name = "net-read" };
            var writer = new Thread(WriteLoop) { IsBackground = true, Name = "net-write" };
            reader.Start();
            writer.Start();
        }

        public static Connection Connect(string host, int port, int timeoutMs = 5000)
        {
            var tcp = new TcpClient();
            IAsyncResult ar = tcp.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                tcp.Close();
                throw new TimeoutException("Connection to " + host + ":" + port + " timed out");
            }
            tcp.EndConnect(ar);
            return new Connection(tcp);
        }

        public void Send(byte[] payload)
        {
            if (_closed) return;
            if (!_outgoing.TryAdd(payload))
                Close("send queue overflow");
        }

        public bool TryReceive(out byte[] payload)
        {
            return _incoming.TryDequeue(out payload);
        }

        public void Close(string reason)
        {
            if (_closed) return;
            _closeReason = reason;
            _closed = true;
            try { _outgoing.CompleteAdding(); } catch { }
            try { _stream.Close(); } catch { }
            try { _tcp.Close(); } catch { }
        }

        private void ReadLoop()
        {
            var header = new byte[4];
            try
            {
                while (!_closed)
                {
                    if (!ReadExact(header, 4)) { Close("remote closed"); return; }
                    int len = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
                    if (len <= 0 || len > MaxFrame) { Close("bad frame length " + len); return; }
                    var buf = new byte[len];
                    if (!ReadExact(buf, len)) { Close("remote closed"); return; }
                    _incoming.Enqueue(buf);
                }
            }
            catch (Exception e)
            {
                Close(e.GetType().Name + ": " + e.Message);
            }
        }

        private bool ReadExact(byte[] buf, int len)
        {
            int read = 0;
            while (read < len)
            {
                int n = _stream.Read(buf, read, len - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        private void WriteLoop()
        {
            var header = new byte[4];
            try
            {
                foreach (byte[] payload in _outgoing.GetConsumingEnumerable())
                {
                    int len = payload.Length;
                    header[0] = (byte)len; header[1] = (byte)(len >> 8); header[2] = (byte)(len >> 16); header[3] = (byte)(len >> 24);
                    _stream.Write(header, 0, 4);
                    _stream.Write(payload, 0, len);
                }
            }
            catch (Exception e)
            {
                Close(e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
