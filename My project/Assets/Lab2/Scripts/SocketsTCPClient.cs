using System;
using System.Net;
using System.Net.Sockets;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

// Escena S_JoinGame_TCP: nombre + IP + boton Join.
// Conecta en un hilo (la ventana no se congela), enseña el motivo si falla y,
// si va bien, manda JOIN y pasa a la sala.
public class SocketsTCPClient : MonoBehaviour
{
    public TMP_InputField inputName;
    public TMP_InputField inputIP;
    public int port = 9050;

    [Header("Bonus")]
    public TextMeshProUGUI statusText;       // opcional: si es null se pinta con OnGUI
    public string roomSceneName = "S_Room_TCP";

    Socket m_pending;
    string m_ip = "";
    string m_name = "";
    string m_connectError = "";
    string m_status = "";
    volatile int m_state;                     // 0 libre, 1 conectando, 2 conectado, 3 fallo

    void Start()
    {
        Application.runInBackground = true;

        // Si venimos de ser expulsados / sala cerrada, lo mostramos
        if (!string.IsNullOrEmpty(LobbyData.LastMessage))
        {
            SetStatus(LobbyData.LastMessage);
            LobbyData.LastMessage = "";
        }
    }

    public void ConnectToServer()
    {
        if (m_state == 1) return;

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

        m_ip = ip;
        m_name = LobbyNet.CleanName(rawName, 16);
        m_state = 1;
        SetStatus("Conectando a " + ip + ":" + port + " ...");

        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        m_pending = socket;
        IPEndPoint endpoint = new IPEndPoint(address, port);
        LobbyNet.StartThread(delegate { ConnectThread(socket, endpoint); });
    }

    void ConnectThread(Socket socket, IPEndPoint endpoint)
    {
        try
        {
            IAsyncResult result = socket.BeginConnect(endpoint, null, null);
            if (!result.AsyncWaitHandle.WaitOne(3000))
            {
                LobbyNet.CloseSocket(socket);
                m_connectError = "tiempo de espera agotado. Revisa la IP, el firewall del servidor (puerto " + port +
                                 ") y que la red no aisle los dispositivos";
                m_state = 3;
                return;
            }

            socket.EndConnect(result);
            socket.NoDelay = true;
            m_state = 2;
        }
        catch (Exception e)
        {
            LobbyNet.CloseSocket(socket);
            m_connectError = e.Message;
            m_state = 3;
        }
    }

    void Update()
    {
        if (m_state == 2)
        {
            m_state = 0;

            SocketsClientRoom.s_activeConnection = m_pending;
            SocketsClientRoom.s_playerName = m_name;
            SocketsClientRoom.s_serverIp = m_ip;

            try { m_pending.Send(LobbyNet.Frame("JOIN:" + m_name)); }
            catch (Exception e)
            {
                LobbyNet.CloseSocket(m_pending);
                SocketsClientRoom.s_activeConnection = null;
                m_pending = null;
                SetStatus("No se pudo enviar JOIN: " + e.Message);
                return;
            }

            m_pending = null;     // desde ahora el socket es de la escena de la sala
            SceneManager.LoadScene(roomSceneName);
        }
        else if (m_state == 3)
        {
            m_state = 0;
            m_pending = null;
            SetStatus("No se pudo conectar: " + m_connectError);
        }
    }

    void SetStatus(string text)
    {
        m_status = text;
        if (statusText != null) statusText.text = text;
        Debug.Log(text);
    }

    void OnGUI()
    {
        if (statusText == null) LobbyGui.DrawStatus(m_status);
    }

    void OnDestroy()
    {
        // Si salimos sin haber pasado a la sala, no dejamos un socket abierto
        if (m_pending != null) LobbyNet.CloseSocket(m_pending);
    }
}
