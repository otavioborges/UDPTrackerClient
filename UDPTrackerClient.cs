using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Net.Sockets;
using System.Net;
using System.IO;

namespace UDPTracker
{
    public enum AnnouceEvents
    {
        NONE = 0, COMPLETED = 1, STARTED = 2, STOPPED = 3
    }

    class ReceiveState
    {
        public Socket m_socketObj;
        public bool m_receivedFlag;

        public ReceiveState(Socket socket, bool flag)
        {
            m_socketObj = socket;
            m_receivedFlag = flag;
        }
    }

    public class UDPTrackerClient : IDisposable
    {
        private readonly int MAX_RESPONSE_SIZE = 6553500;
        private readonly long MAGIC_NUMBER = 0x41727101980;
        private readonly int MAX_RETRIES = 3;
        private readonly int[] EXPECTED_TIMEOUT = { 15000, 30000, 60000, 120000, 240000, 480000, 960000, 1920000, 3840000 };

        private Socket m_client;
        private long m_connectionID;
        private string m_server;
        private short m_port;
        private Random m_randomGenerator;
        private bool m_connected;
        private DateTime m_connectionTime;
        private bool m_received;
        private bool m_disposed;

        #region Constructors
        public UDPTrackerClient(string server, short port)
        {
            try
            {
                m_client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            }
            catch(Exception ex)
            {
                throw new UDPClientException("Error trying to create socket", ex, UDPClientError.SocketError);
            }
            m_connectionID = 0;
            m_server = server;
            m_port = port;

            m_randomGenerator = new Random((int)DateTime.Now.Ticks);
            m_connected = false;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!m_disposed)
            {
                if (disposing)
                {
                    m_client.Dispose();
                    m_connected = false;
                }
                m_randomGenerator = null;
                m_disposed = true;
            }
        }

        ~UDPTrackerClient()
        {
            Dispose(true);
        }
        #endregion

        #region Public Methods
        public void Close()
        {
            if (m_client.Connected)
            {
                try
                {
                    m_client.Close(1);
                    m_connected = false;
                }
                catch (Exception ex)
                {
                    throw new UDPClientException("Error trying to disconnect", ex, UDPClientError.SocketError, m_server, m_port);
                }
            }
        }

        public void Connect()
        {
            try
            {
                m_client.Connect(m_server, m_port);
            }
            catch(Exception ex)
            {
                throw new UDPClientException("Error trying to connect socket", ex, UDPClientError.BadServer, m_server, m_port);
            }

            List<byte> request = new List<byte>();

            int sendTransaction = NextTransactionID();
            Append(MAGIC_NUMBER, ref request, true);
            Append(0, ref request, false);
            Append(sendTransaction, ref request, false);

            byte[] response = Transmit(request.ToArray(), 16);
            if (response == null) // no response from request
                throw new UDPClientException("No response from server on connect command", null, UDPClientError.BadServer, m_server, m_port);

            int recvTransaction = BitConverter.ToInt32(response, 4);

            if (sendTransaction != recvTransaction)
                throw new UDPClientException("Bad transaction_id received from server", null, UDPClientError.BadTransactionID, m_server, m_port);

            m_connectionID = BitConverter.ToInt64(response, 8);
            m_connectionTime = DateTime.Now;
            m_connected = true;
        }

        public List<IPEndPoint> Announce(byte[] infoHash, byte[] peerID)
        {
            List<IPEndPoint> peers = new List<IPEndPoint>();

            if (!m_connected)
                throw new UDPClientException("Trying Announce on disconnected API", null, UDPClientError.ConnectionClosed, m_server, m_port);

            if (infoHash.Length != 20)
                throw new UDPClientException("Invalid torrent info hash", UDPClientError.BadInfoHash);
            if (peerID.Length != 20)
                throw new UDPClientException("Invalid peer ID", UDPClientError.BadPeerID);

            // We're good to go! Using none as event
            if((DateTime.Now - m_connectionTime).TotalSeconds > 107)
            {
                // connectionID probably expired, reconnect
                try
                {
                    Connect();
                }
                catch(UDPClientException ex)
                {
                    throw ex;
                }
            }

            int sendTransaction = NextTransactionID();
            int announceKey = NextTransactionID();

            List<byte> request = new List<byte>();

            Append(m_connectionID, ref request, false);         // connection_id
            Append(1, ref request, true);                      // action
            Append(sendTransaction, ref request, false);        // transaction_id
            Append(infoHash, ref request, false);               // torrent info_hash
            Append(peerID, ref request, false);                 // peer_id
            Append(0L, ref request, false);                     // downloaded
            Append(0L, ref request, false);                     // left
            Append(0L, ref request, false);                     // uploaded
            Append((int)AnnouceEvents.NONE, ref request, false);// event
            Append(0, ref request, false);                      // IP Address
            Append(announceKey, ref request, false);            // key
            Append(50, ref request, true);                     // num_want (-1 for default)
            Append((short)15000, ref request, false);           // client listening port

            byte[] response = Transmit(request.ToArray(), MAX_RESPONSE_SIZE);

            if (response == null) // no response from tracker
                throw new UDPClientException("No response from server to Announce request", null, UDPClientError.ResponseTimeout, m_server, m_port);

            // catch the remaining EndPoints
            int totalPeers = BitConverter.ToInt32(ReverseChunk(response, 12, 4), 0); // add leechers
            totalPeers += BitConverter.ToInt32(ReverseChunk(response,16,4), 0); // add seeders

            byte[] endPoint = new byte[6]; // IP and port
            byte[] seedIP = new byte[4];
            int seedPort = -1;

            int totalDesiredSize = 20 + (6 * (totalPeers));
            if (response.Length < totalDesiredSize)
                throw new UDPClientException("Wrong response size, missing chunks", null, UDPClientError.BadResponse, m_server, m_port);

            // Add all peers to list
            for(int seed = 0; seed < totalPeers; seed++)
            {
                Array.Copy(response, 20 + (6 * seed), endPoint, 0, 6);

                Array.Copy(endPoint, 0, seedIP, 0, 4);
                seedPort = (int)BitConverter.ToUInt16(ReverseChunk(endPoint, 4, 2), 0);
                peers.Add(new IPEndPoint(new IPAddress(seedIP), seedPort));
            }

            return peers;
        }

        public int GetPeersForTorrent(byte[] infoHash)
        {
            int peers = -1;

            if (!m_connected)
                throw new UDPClientException("Trying Announce on disconnected API", null, UDPClientError.ConnectionClosed, m_server, m_port);

            if (infoHash.Length != 20)
                throw new UDPClientException("Invalid torrent info hash", UDPClientError.BadInfoHash);

            // We're good to go! Using none as event
            if ((DateTime.Now - m_connectionTime).TotalSeconds > 107)
            {
                // connectionID probably expired, reconnect
                try
                {
                    Connect();
                }
                catch (UDPClientException ex)
                {
                    throw ex;
                }
            }

            int sendTransaction = NextTransactionID();
            List<byte> request = new List<byte>();

            Append(m_connectionID, ref request, false);         // connection_id
            Append(2, ref request, true);                      // action
            Append(sendTransaction, ref request, false);        // transaction_id
            Append(infoHash, ref request, false);               // torrent info_hash

            byte[] response = Transmit(request.ToArray(), 20);
            if (response == null) // no response from tracker
                throw new UDPClientException("No response from server to Announce request", null, UDPClientError.ResponseTimeout, m_server, m_port);

            int recvTransaction = BitConverter.ToInt32(response, 4);
            if (sendTransaction != recvTransaction)
                throw new UDPClientException("Bad transaction_id received from server", null, UDPClientError.BadTransactionID, m_server, m_port);

            // catch the remaining EndPoints
            peers = BitConverter.ToInt32(ReverseChunk(response,8,4), 0);
            return peers;
        }
        #endregion

        #region Socket Transmission
        private static void ReceiveCallback(IAsyncResult ar)
        {
            ReceiveState state = (ReceiveState)ar.AsyncState;
            try
            {
                state.m_socketObj.EndReceive(ar);
                state.m_receivedFlag = true;
            }
            catch(Exception ex)
            {
                throw new UDPClientException("Error trying to get response from server", ex, UDPClientError.BadResponse);
            }
        }

        private byte[] Transmit(byte[] request, int responseSize)
        {
            ReceiveState state = new ReceiveState(m_client, m_received);
            int retry = 0;

            if (m_client.Connected == false)
                return null;

            byte[] response = new byte[MAX_RESPONSE_SIZE];
            List<ArraySegment<byte>> recvBuffers = new List<ArraySegment<byte>>();
            for(int seg = 0; seg < 100; seg++)
            {
                recvBuffers.Add(new ArraySegment<byte>(response, seg * 65535, 65535));
            }
            while(retry <= MAX_RETRIES)
            {
                m_client.Send(request);

                m_client.DontFragment = true;
                m_received = false;
                m_client.BeginReceive(recvBuffers, SocketFlags.None,
                    new AsyncCallback(ReceiveCallback), state);

                DateTime transmitBegin = DateTime.Now;
                TimeSpan timeWaiting = new TimeSpan(0,0,0);
                while((timeWaiting.TotalMilliseconds < EXPECTED_TIMEOUT[retry]) && (!state.m_receivedFlag))
                {
                    System.Threading.Thread.Sleep(1000);
                    timeWaiting = DateTime.Now - transmitBegin;
                }
                m_received = state.m_receivedFlag;

                if (m_received == false)
                    retry++;
                else
                    break;
            }

            m_received = false;
            if (retry > MAX_RETRIES)
                return null;            // we exhaust tentatives return null
            else
                return response;
        }
        #endregion

        #region Private Methods
        private int NextTransactionID()
        {
            return m_randomGenerator.Next();
        }

        private void Append(int value, ref List<byte> buffer, bool reverse)
        {
            byte[] valueInBytes = BitConverter.GetBytes(value);

            if (reverse)
                buffer.AddRange(valueInBytes.Reverse().ToArray());
            else
                buffer.AddRange(valueInBytes);
        }

        private void Append(long value, ref List<byte> buffer, bool reverse)
        {
            byte[] valueInBytes = BitConverter.GetBytes(value);

            if (reverse)
                buffer.AddRange(valueInBytes.Reverse().ToArray());
            else
                buffer.AddRange(valueInBytes);
        }

        private void Append(short value, ref List<byte> buffer, bool reverse)
        {
            byte[] valueInBytes = BitConverter.GetBytes(value);

            if (reverse)
                buffer.AddRange(valueInBytes.Reverse().ToArray());
            else
                buffer.AddRange(valueInBytes);
        }

        private void Append(byte[] value, ref List<byte> buffer, bool reverse)
        {
            if (reverse)
                buffer.AddRange(value.Reverse().ToArray());
            else
                buffer.AddRange(value);
        }

        private byte[] ReverseChunk(byte[] buffer, int startIndex, int length)
        {
            byte[] rtnArray = new byte[length];

            Array.Copy(buffer, startIndex, rtnArray, 0, length);

            return rtnArray.Reverse().ToArray();
        }
        #endregion
    }
}
