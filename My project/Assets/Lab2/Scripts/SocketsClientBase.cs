using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Logica de cliente compartida por TCP (sala) y UDP. El transporte lo ponen las subclases.
// Los hilos de red solo encolan texto en m_inbox; todo lo que toca Unity ocurre en Update().
public abstract class SocketsClientBase : MonoBehaviour
{
    public TMP_InputField inputChat;
    public TextMeshProUGUI chatHistoryText;
    public TextMeshProUGUI playersListText;
    public int port = 9050;

    [Header("Bonus")]
    public TextMeshProUGUI statusText;               // mensajes de estado y errores
    public ScrollRect chatScroll;                    // opcional: el chat baja solo al ultimo mensaje
    public string gameSceneName = "S_Game";
    public string joinSceneName = "S_JoinGame_TCP";

    protected readonly ConcurrentQueue<string> m_inbox = new ConcurrentQueue<string>();
    protected volatile bool m_running;   // conexion activa
    protected volatile bool m_lost;      // el hilo de recepcion termino sin que lo pidieramos

    string[] m_names = new string[0];
    readonly Dictionary<string, int> m_pings = new Dictionary<string, int>();
    int m_maxPlayers;
    string m_status = "";
    long m_lastHeardMs;
    bool m_serverHeartbeat;              // solo vigilamos al servidor si el nuestro manda SPING

    // ---------- Lo que implementa cada transporte ----------
    protected abstract string PlayerName { get; }
    public abstract void SendString(string text);
    protected abstract void CloseConnection();                 // idempotente
    protected virtual void OnServerMessage() { }

    // ---------- Bucle principal (lo llama el Update de la subclase) ----------
    protected void ProcessInbox()
    {
        string msg;
        int guard = 0;
        while (guard++ < 500 && m_inbox.TryDequeue(out msg))
        {
            if (m_running) HandleMessage(msg);
        }

        if (!m_running) return;

        if (m_lost)
        {
            m_lost = false;
            Disconnected("Se ha perdido la conexion con el servidor", false);
            return;
        }

        if (m_serverHeartbeat && LobbyNet.NowMs - m_lastHeardMs > 5000)
            Disconnected("El servidor ha dejado de responder", false);
    }

    protected void MarkConnected()
    {
        m_lastHeardMs = LobbyNet.NowMs;
        m_serverHeartbeat = false;
        m_lost = false;
    }

    void HandleMessage(string text)
    {
        m_lastHeardMs = LobbyNet.NowMs;
        OnServerMessage();

        if (text.StartsWith("PLAYERS:"))
        {
            m_names = ParseList(text.Substring(8));
            RefreshPlayers();
        }
        else if (text.StartsWith("PINGS:"))
        {
            foreach (string entry in text.Substring(6).Split(','))
            {
                int eq = entry.LastIndexOf('=');
                int value;
                if (eq > 0 && int.TryParse(entry.Substring(eq + 1), out value))
                    m_pings[entry.Substring(0, eq).Trim()] = value;
            }
            RefreshPlayers();
        }
        else if (text.StartsWith("MAX:"))
        {
            int max;
            if (int.TryParse(text.Substring(4), out max)) m_maxPlayers = max;
            RefreshPlayers();
        }
        else if (text.StartsWith("CHAT:"))
        {
            AddChat(text.Substring(5));
        }
        else if (text.StartsWith("SPING:"))
        {
            m_serverHeartbeat = true;
            SendString("SPONG:" + text.Substring(6));
        }
        else if (text.StartsWith("START:"))
        {
            GoToGame();
        }
        else if (text.StartsWith("KICK:"))
        {
            Disconnected("Has sido expulsado de la sala", false);
        }
        else if (text.StartsWith("CLOSED:"))
        {
            Disconnected("El host ha cerrado la sala", false);
        }
        else if (text.StartsWith("FULL:"))
        {
            Disconnected("La sala esta llena o la partida ya ha empezado", false);
        }
    }

    // ---------- Salir ----------
    public void LeaveRoom() { Disconnected(null, true); }   // boton Leave

    protected void Disconnected(string message, bool sendLeave)
    {
        if (!m_running) return;
        if (sendLeave) SendString("LEAVE:");

        m_running = false;
        CloseConnection();

        m_names = new string[0];
        m_pings.Clear();
        m_maxPlayers = 0;
        m_serverHeartbeat = false;

        OnLeftRoom(message);
    }

    protected virtual void OnLeftRoom(string message)
    {
        LobbyData.LastMessage = message ?? "";
        if (playersListText != null) playersListText.text = "";

        // En la escena de sala o de juego volvemos al menu de unirse; en la propia escena de
        // unirse (UDP) nos quedamos y mostramos el motivo.
        if (SceneManager.GetActiveScene().name != joinSceneName && Application.CanStreamedLevelBeLoaded(joinSceneName))
        {
            SceneManager.LoadScene(joinSceneName);
            Destroy(gameObject);
            return;
        }
        SetStatus(string.IsNullOrEmpty(message) ? "Has salido de la sala" : message);
    }

    protected void GoToGame()
    {
        if (!Application.CanStreamedLevelBeLoaded(gameSceneName))
        {
            SetStatus("El host ha iniciado la partida pero la escena '" + gameSceneName + "' no esta en Build Settings");
            return;
        }

        LobbyData.PlayerNames = m_names;
        LobbyData.LocalName = PlayerName;
        LobbyData.IsHost = false;

        DontDestroyOnLoad(gameObject);     // la conexion sigue viva en la escena de juego
        SceneManager.LoadScene(gameSceneName);
    }

    // ---------- Chat ----------
    public void SendChatMessage()
    {
        if (inputChat == null || string.IsNullOrEmpty(inputChat.text)) return;
        string text = inputChat.text.Trim();
        inputChat.text = "";
        if (text.Length == 0) return;
        SendString("CHAT:" + text);
    }

    // ---------- UI ----------
    static string[] ParseList(string s)
    {
        if (s.Trim().Length == 0) return new string[0];
        string[] parts = s.Split(',');
        for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();   // acepta "A,B" y "A, B"
        return parts;
    }

    void AddChat(string line)
    {
        if (chatHistoryText == null) return;
        chatHistoryText.text += "\n" + line;
        if (chatScroll != null)
        {
            Canvas.ForceUpdateCanvases();
            chatScroll.verticalNormalizedPosition = 0f;
        }
    }

    void RefreshPlayers()
    {
        if (playersListText == null) return;

        StringBuilder sb = new StringBuilder("Jugadores");
        if (m_maxPlayers > 0) sb.Append(" (").Append(m_names.Length).Append('/').Append(m_maxPlayers).Append(')');
        sb.Append(':');

        foreach (string name in m_names)
        {
            sb.Append('\n').Append(name);
            int ping;
            if (m_pings.TryGetValue(name, out ping) && ping >= 0) sb.Append(" - ").Append(ping).Append(" ms");
        }
        playersListText.text = sb.ToString();
    }

    protected void SetStatus(string text)
    {
        m_status = text;
        if (statusText != null) statusText.text = text;
        Debug.Log(text);
    }

    void OnGUI()
    {
        if (statusText == null) LobbyGui.DrawStatus(m_status);
    }

    // ---------- Cierre limpio ----------
    void OnDestroy()
    {
        m_running = false;
        CloseConnection();
    }

    void OnApplicationQuit()
    {
        if (m_running) SendString("LEAVE:");
        m_running = false;
        CloseConnection();
    }
}