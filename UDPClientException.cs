using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UDPTracker
{
    public enum UDPClientError
    {
        BadPort, BadServer, RequestTimeout, ResponseTimeout, ConnectionClosed, SocketError, Unknown,
        BadInfoHash, BadPeerID
    }

    public class UDPClientException : Exception
    {
        private UDPClientError m_error;
        private string m_server;
        private short m_port;

        #region Constructors
        public UDPClientException(string message, UDPClientError error)
            : base(message)
        {
            m_error = error;
            m_server = string.Empty;
            m_port = 0;
        }

        public UDPClientException(string message, Exception innerException, UDPClientError error)
            : base(message, innerException)
        {
            m_error = error;
            m_server = string.Empty;
            m_port = 0;
        }

        public UDPClientException(string message, Exception innerException, UDPClientError error, string server, short port)
            : base(message, innerException)
        {
            m_error = error;
            m_server = server;
            m_port = port;
        }
        #endregion

        #region Properties
        public UDPClientError Error { get { return m_error; } }
        public string Server { get { return m_server; } }
        public short Port { get { return m_port; } }
        #endregion
    }
}
