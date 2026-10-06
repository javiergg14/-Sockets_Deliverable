using System;
using System.Net.Sockets;
using UnityEngine;

// Escena S_Room_TCP: recoge el socket que abrio la escena de unirse y lo mantiene vivo.
public class SocketsClientRoom : SocketsClientBase
{
    // Los rellena SocketsTCPClient antes de cargar la escena de la sala
    public static Socket s_activeConnection;
    public static string s_playerName;
    public static string s_serverIp;

    Socket m_socket;

    public SocketsClientRoom() { joinSceneName = "S_JoinGame_TCP"; }

    protected override string PlayerName { get { return s_playerName; } }

    void Start()
    {
        Application.runInBackground = true;

        m_socket = s_activeConnection;
        if (m_socket == null || !m_socket.Connected)
        {
            OnLeftRoom("No hay conexion con el servidor");
            return;
        }

        m_running = true;
        MarkConnected();
        SetStatus("Conectado a " + s_serverIp);

        Socket socket = m_socket;
        LobbyNet.StartThread(delegate { ReceiveLoop(socket); });
    }

    void ReceiveLoop(Socket socket)
    {
        string text;
        while (m_running && LobbyNet.ReadFramed(socket, out text)) m_inbox.Enqueue(text);

        // Si seguimos "running", nadie nos pidio cerrar: el servidor se ha ido
        if (m_running) m_lost = true;
    }

    void Update() { ProcessInbox(); }

    public override void SendString(string text)
    {
        if (m_socket == null) return;
        try { m_socket.Send(LobbyNet.Frame(text)); }
        catch (Exception) { }
    }

    protected override void CloseConnection()
    {
        LobbyNet.CloseSocket(m_socket);
        m_socket = null;
        s_activeConnection = null;
    }
}
