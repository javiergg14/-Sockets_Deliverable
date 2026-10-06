using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

// Transporte TCP del servidor. La logica de la sala esta en SocketsServerBase.
// Un hilo para Accept y un hilo por cliente (opcion 1 de la diapositiva 3).
public class SocketsTCPServer : SocketsServerBase
{
    Socket m_listener;
    readonly List<Socket> m_sockets = new List<Socket>();   // solo se toca desde el hilo principal

    public SocketsTCPServer() { createSceneName = "S_CreateGame_TCP"; }

    protected override string TransportName { get { return "Servidor TCP"; } }
    protected override bool UsesHeartbeatTimeout { get { return false; } }   // TCP avisa solo al cerrarse

    protected override string OpenNetwork()
    {
        try
        {
            m_listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            m_listener.Bind(new IPEndPoint(IPAddress.Any, port));
            m_listener.Listen(10);
        }
        catch (Exception e)
        {
            LobbyNet.CloseSocket(m_listener);
            m_listener = null;
            return e.Message;
        }

        Socket listener = m_listener;
        LobbyNet.StartThread(delegate { AcceptLoop(listener); });
        return null;
    }

    void AcceptLoop(Socket listener)
    {
        while (m_running)
        {
            Socket client;
            try { client = listener.Accept(); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException)
            {
                if (!m_running) return;
                Thread.Sleep(50);
                continue;
            }

            try { client.NoDelay = true; client.SendTimeout = 1000; } catch (Exception) { }

            m_events.Enqueue(new NetEvent { type = NetEventType.Opened, conn = client });
            LobbyNet.StartThread(delegate { ClientLoop(client); });
        }
    }

    void ClientLoop(Socket client)
    {
        string text;
        while (m_running && LobbyNet.ReadFramed(client, out text))
        {
            m_events.Enqueue(new NetEvent
            {
                type = NetEventType.Data,
                conn = client,
                text = text,
                ms = LobbyNet.NowMs
            });
        }
        // Receive devolvio 0 o lanzo excepcion: boton Leave, ventana cerrada o caida
        m_events.Enqueue(new NetEvent { type = NetEventType.Closed, conn = client });
    }

    protected override void OnOpened(object conn) { m_sockets.Add((Socket)conn); }

    protected override void OnForget(object conn)
    {
        Socket s = conn as Socket;
        if (s == null) return;
        m_sockets.Remove(s);
        LobbyNet.CloseSocket(s);
    }

    protected override void SendTo(object conn, string text)
    {
        Socket s = conn as Socket;
        if (s == null) return;
        try { s.Send(LobbyNet.Frame(text)); }
        catch (Exception) { }   // si falla, el hilo de ese cliente lo detecta y genera Closed
    }

    protected override void DropConnection(object conn) { LobbyNet.CloseSocket(conn as Socket); }

    protected override void CloseNetwork()
    {
        foreach (Socket s in m_sockets.ToArray()) LobbyNet.CloseSocket(s);
        m_sockets.Clear();
        LobbyNet.CloseSocket(m_listener);
        m_listener = null;
    }
}
