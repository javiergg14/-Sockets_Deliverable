using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TMPro;
using UnityEngine;

// Cliente UDP: unirse y sala en la misma escena (S_JoinGame_UDP).
// UDP no garantiza nada, asi que el JOIN se reintenta hasta que el servidor contesta,
// y se manda PING: cada segundo para que el servidor sepa que seguimos vivos.
public class SocketsUDPClient : SocketsClientBase
{
    public TMP_InputField inputName;
    public TMP_InputField inputIP;

    [Header("Paneles (opcional)")]
    public GameObject joinPanel;      // nombre + IP + Unirse
    public GameObject roomPanel;      // lista de jugadores + chat + Salir

    Socket m_socket;
    EndPoint m_serverEndPoint;
    string m_playerName = "";
    volatile bool m_confirmed;        // el servidor ya nos ha contestado
    long m_lastJoinMs;
    int m_joinAttempts;

    public SocketsUDPClient() { joinSceneName = "S_JoinGame_UDP"; }

    protected override string PlayerName { get { return m_playerName; } }

    void Start()
    {
        Application.runInBackground = true;
        ShowRoom(false);
        if (!string.IsNullOrEmpty(LobbyData.LastMessage))
        {
            SetStatus(LobbyData.LastMessage);
            LobbyData.LastMessage = "";
        }
    }

    void ShowRoom(bool inRoom)
    {
        if (joinPanel != null) joinPanel.SetActive(!inRoom);
        if (roomPanel != null) roomPanel.SetActive(inRoom);
    }

    public void ConnectToServer()
    {
        if (m_running) return;

        string ip = inputIP != null ? inputIP.text.Trim() : "";
        string rawName = inputName != null ? inputName.text.Trim() : "";

        if (ip.Length == 0 || rawName.Length == 0)
        {
            SetStatus("Escribe tu nombre y la IP del servidor");
            return;
        }

        if (ip.ToLower() == "localhost") ip = "127.0.0.1";

        IPAddress address;
        if (!IPAddress.TryParse(ip, out address))
        {
            SetStatus("La IP '" + ip + "' no es valida (ejemplo: 192.168.1.20)");
            return;
        }

        try
        {
            m_socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            LobbyNet.DisableUdpConnReset(m_socket);
            m_serverEndPoint = new IPEndPoint(address, port);
        }
        catch (Exception e)
        {
            SetStatus("No se pudo crear el socket: " + e.Message);
            return;
        }

        m_playerName = LobbyNet.CleanName(rawName, 16);
        m_confirmed = false;
        m_joinAttempts = 0;
        m_lastJoinMs = -1000;           // el primer JOIN sale en el siguiente Update
        m_running = true;
        MarkConnected();
        SetStatus("Conectando a " + ip + ":" + port + " ...");

        Socket socket = m_socket;
        LobbyNet.StartThread(delegate { ReceiveLoop(socket); });
        LobbyNet.StartThread(PingLoop);
    }

    void Update()
    {
        ProcessInbox();

        if (!m_running || m_confirmed) return;

        long now = LobbyNet.NowMs;
        if (now - m_lastJoinMs < 1000) return;

        if (m_joinAttempts >= 5)
        {
            Disconnected("El servidor no responde. Revisa la IP, el firewall (UDP " + port + ") y que ambos esteis en la misma red", false);
            return;
        }

        m_joinAttempts++;
        m_lastJoinMs = now;
        SendString("JOIN:" + m_playerName);
    }

    protected override void OnServerMessage()
    {
        if (!m_confirmed)
        {
            m_confirmed = true;
            SetStatus("Conectado");
            ShowRoom(true);
        }
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
                if (e.SocketErrorCode == SocketError.ConnectionReset) continue;
                m_lost = true;
                return;
            }

            if (received > 0) m_inbox.Enqueue(Encoding.UTF8.GetString(buffer, 0, received));
        }
    }

    void PingLoop()
    {
        while (m_running)
        {
            if (m_confirmed) SendString("PING:");
            Thread.Sleep(1000);
        }
    }

    public override void SendString(string text)
    {
        Socket socket = m_socket;
        EndPoint server = m_serverEndPoint;
        if (socket == null || server == null) return;

        try { socket.SendTo(Encoding.UTF8.GetBytes(text), server); }
        catch (Exception) { }
    }

    protected override void OnLeftRoom(string message)
    {
        ShowRoom(false);
        base.OnLeftRoom(message);
    }

    protected override void CloseConnection()
    {
        LobbyNet.CloseSocket(m_socket);
        m_socket = null;
    }
}