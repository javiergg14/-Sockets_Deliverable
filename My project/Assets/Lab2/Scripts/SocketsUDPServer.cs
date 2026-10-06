using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// Transporte UDP del servidor. La logica de la sala esta en SocketsServerBase.
// UDP no avisa cuando alguien se va: la base expulsa a quien lleve mas de 5 s sin dar señales
// (los clientes mandan PING: cada segundo).
public class SocketsUDPServer : SocketsServerBase
{
    Socket m_socket;

    public SocketsUDPServer() { createSceneName = "S_CreateGame_UDP"; }

    protected override string TransportName { get { return "Servidor UDP"; } }
    protected override bool UsesHeartbeatTimeout { get { return true; } }

    protected override string OpenNetwork()
    {
        try
        {
            m_socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            m_socket.Bind(new IPEndPoint(IPAddress.Any, port));
            LobbyNet.DisableUdpConnReset(m_socket);
        }
        catch (Exception e)
        {
            LobbyNet.CloseSocket(m_socket);
            m_socket = null;
            return e.Message;
        }

        Socket socket = m_socket;
        LobbyNet.StartThread(delegate { ReceiveLoop(socket); });
        return null;
    }

    void ReceiveLoop(Socket socket)
    {
        byte[] buffer = new byte[LobbyNet.MaxPacketSize];

        while (m_running)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int received;
            try { received = socket.ReceiveFrom(buffer, ref from); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException e)
            {
                if (!m_running) return;
                if (e.SocketErrorCode == SocketError.ConnectionReset) continue;   // un cliente cerrado: no es un error nuestro
                Thread.Sleep(10);
                continue;
            }

            if (received <= 0) continue;

            m_events.Enqueue(new NetEvent
            {
                type = NetEventType.Data,
                conn = from,
                text = Encoding.UTF8.GetString(buffer, 0, received),
                ms = LobbyNet.NowMs
            });
        }
    }

    protected override void SendTo(object conn, string text)
    {
        EndPoint ep = conn as EndPoint;
        if (ep == null || m_socket == null) return;
        try { m_socket.SendTo(Encoding.UTF8.GetBytes(text), ep); }
        catch (Exception) { }
    }

    protected override void CloseNetwork()
    {
        LobbyNet.CloseSocket(m_socket);
        m_socket = null;
    }
}
