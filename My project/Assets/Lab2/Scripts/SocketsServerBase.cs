using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Logica de la sala, igual para TCP y UDP. Solo cambia el "transporte" (las subclases).
//
// Regla de oro: los hilos de red SOLO meten NetEvent en m_events. Todo lo demas (lista de
// jugadores, chat, envios, UI) ocurre en Update(), asi que no hacen falta locks y no hay
// "Collection was modified" ni jugadores fantasma.
public abstract class SocketsServerBase : MonoBehaviour
{
    [Header("Sala")]
    public TextMeshProUGUI playersText;
    public TMP_InputField inputChat;
    public TextMeshProUGUI chatHistoryText;
    public int port = 9050;
    public string serverName = "Host";

    [Header("Bonus")]
    public int maxPlayers = 8;                       // incluye al host
    public string gameSceneName = "S_Game";
    public string createSceneName = "S_CreateGame_TCP";
    public TMP_InputField inputKick;                 // campo de texto del boton Expulsar (KickPlayer)
    public TextMeshProUGUI statusText;               // mensajes de estado y errores
    public ScrollRect chatScroll;                    // opcional: el chat baja solo al ultimo mensaje
    public bool bonusMessages = true;                // false = solo mensajes de la diapositiva 5

    protected const int TimeoutMs = 5000;
    protected readonly ConcurrentQueue<NetEvent> m_events = new ConcurrentQueue<NetEvent>();
    protected volatile bool m_running;

    readonly List<LobbyPlayer> m_players = new List<LobbyPlayer>();
    long m_nextPingMs;
    string m_status = "";
    bool m_gameStarted;

    // ---------- Lo que implementa cada transporte ----------
    protected abstract string TransportName { get; }
    protected abstract bool UsesHeartbeatTimeout { get; }
    protected abstract string OpenNetwork();                 // null = bien, texto = error
    protected abstract void CloseNetwork();
    protected abstract void SendTo(object conn, string text);
    protected virtual void DropConnection(object conn) { }
    protected virtual void OnOpened(object conn) { }
    protected virtual void OnForget(object conn) { }

    // ---------- Ciclo de vida ----------
    void Start()
    {
        Application.runInBackground = true;
        StartNetwork();
    }

    public void StartNetwork()
    {
        if (m_running) return;
        m_running = true;

        string error = OpenNetwork();
        if (error != null)
        {
            m_running = false;
            SetStatus("No se pudo iniciar el servidor en el puerto " + port + ": " + error);
            return;
        }

        SetStatus(TransportName + " abierto en el puerto " + port + ". IP para los clientes: " + LobbyNet.LocalIps());
        RefreshUI();
    }

    public void Disconnect()
    {
        if (!m_running) return;
        Broadcast("CLOSED:");
        m_running = false;
        m_players.Clear();
        CloseNetwork();
    }

    void OnDestroy() { Disconnect(); }
    void OnApplicationQuit() { Disconnect(); }

    void Update()
    {
        NetEvent ev;
        int guard = 0;
        while (guard++ < 500 && m_events.TryDequeue(out ev)) HandleEvent(ev);

        if (!m_running) return;

        long now = LobbyNet.NowMs;
        if (UsesHeartbeatTimeout) CheckTimeouts(now);

        if (now >= m_nextPingMs)
        {
            m_nextPingMs = now + 1000;
            PingTick(now);
        }
    }

    // ---------- Eventos de red ----------
    void HandleEvent(NetEvent ev)
    {
        if (!m_running) return;

        if (ev.type == NetEventType.Opened)
        {
            OnOpened(ev.conn);
        }
        else if (ev.type == NetEventType.Closed)
        {
            LobbyPlayer p = FindPlayer(ev.conn);
            if (p != null) RemovePlayer(p, "se ha desconectado");
            OnForget(ev.conn);
        }
        else
        {
            HandleData(ev);
        }
    }

    void HandleData(NetEvent ev)
    {
        string text = ev.text;
        if (text == null) return;

        LobbyPlayer p = FindPlayer(ev.conn);

        if (text.StartsWith("JOIN:"))
        {
            if (p != null)
            {
                p.lastSeenMs = ev.ms;          // JOIN repetido (UDP): solo reenviamos la info
                SendRoomInfoTo(p.conn);
            }
            else
            {
                HandleJoin(ev.conn, text.Substring(5), ev.ms);
            }
            return;
        }

        if (p == null)
        {
            // UDP: un cliente que ya no esta en la lista (timeout o expulsado) sigue enviando.
            // Se lo decimos para que no se quede mirando una sala a la que ya no pertenece.
            if (UsesHeartbeatTimeout) SendTo(ev.conn, "KICK:");
            return;
        }

        p.lastSeenMs = ev.ms;

        if (text.StartsWith("PING:")) return;

        if (text.StartsWith("LEAVE:"))
        {
            RemovePlayer(p, "ha salido");
            return;
        }

        if (text.StartsWith("CHAT:"))
        {
            string msg = text.Substring(5).Replace("\r", " ").Replace("\n", " ").Trim();
            if (msg.Length == 0) return;
            if (msg.Length > 300) msg = msg.Substring(0, 300);

            string line = p.name + ": " + msg;
            AddChatLine(line);
            Broadcast("CHAT:" + line);          // a TODOS, no solo a quien lo escribio
            return;
        }

        if (text.StartsWith("SPONG:"))
        {
            long sent;
            if (long.TryParse(text.Substring(6), out sent))
            {
                long rtt = ev.ms - sent;
                p.pingMs = (int)Math.Max(0, Math.Min(rtt, 9999));
            }
        }
    }

    void HandleJoin(object conn, string rawName, long ms)
    {
        if (m_gameStarted || m_players.Count + 1 >= maxPlayers)   // +1 = el host
        {
            SendTo(conn, "FULL:");
            DropConnection(conn);
            return;
        }

        string name = UniqueName(LobbyNet.CleanName(rawName, 16));
        m_players.Add(new LobbyPlayer { name = name, conn = conn, lastSeenMs = ms });

        string joined = "* " + name + " se ha unido";
        AddChatLine(joined);
        Broadcast("CHAT:" + joined);
        BroadcastPlayerList();
    }

    void RemovePlayer(LobbyPlayer p, string reason)
    {
        m_players.Remove(p);
        DropConnection(p.conn);

        string line = "* " + p.name + " " + reason;
        AddChatLine(line);
        Broadcast("CHAT:" + line);
        BroadcastPlayerList();
    }

    void CheckTimeouts(long now)
    {
        for (int i = m_players.Count - 1; i >= 0; i--)
        {
            if (now - m_players[i].lastSeenMs > TimeoutMs)
                RemovePlayer(m_players[i], "se ha desconectado (timeout)");
        }
    }

    // ---------- Ping por jugador (bonus) ----------
    // El servidor manda SPING:<ms> a cada cliente, el cliente contesta SPONG:<ms>, y la
    // diferencia es el ping. Cada segundo se reparte PINGS:nombre=ms,... a todos.
    void PingTick(long now)
    {
        if (bonusMessages && m_players.Count > 0)
        {
            StringBuilder sb = new StringBuilder("PINGS:");
            for (int i = 0; i < m_players.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(m_players[i].name).Append('=').Append(m_players[i].pingMs);
            }
            Broadcast(sb.ToString());
            Broadcast("SPING:" + now);
        }
        RefreshUI();
    }

    // ---------- Envios ----------
    void Broadcast(string text)
    {
        foreach (LobbyPlayer p in m_players.ToArray()) SendTo(p.conn, text);
    }

    void BroadcastPlayerList()
    {
        if (bonusMessages) Broadcast("MAX:" + maxPlayers);
        Broadcast("PLAYERS:" + string.Join(",", PlayerNames()));
        RefreshUI();
    }

    void SendRoomInfoTo(object conn)
    {
        if (bonusMessages) SendTo(conn, "MAX:" + maxPlayers);
        SendTo(conn, "PLAYERS:" + string.Join(",", PlayerNames()));
    }

    string[] PlayerNames()
    {
        string[] names = new string[m_players.Count + 1];
        names[0] = HostLabel;                       // el host tambien juega: sale en la lista
        for (int i = 0; i < m_players.Count; i++) names[i + 1] = m_players[i].name;
        return names;
    }

    // ---------- Acciones del host (botones de la UI) ----------
    public void SendChatMessage()
    {
        if (inputChat == null || string.IsNullOrEmpty(inputChat.text)) return;

        string text = inputChat.text.Trim();
        inputChat.text = "";
        if (text.Length == 0) return;

        if (text.StartsWith("/kick ")) { KickByName(text.Substring(6)); return; }
        if (text == "/start") { StartGame(); return; }

        string line = HostLabel + ": " + text;
        AddChatLine(line);
        Broadcast("CHAT:" + line);
    }

    public void KickPlayer()
    {
        if (inputKick == null) return;
        KickByName(inputKick.text);
        inputKick.text = "";
    }

    public bool KickByName(string name)
    {
        name = (name ?? "").Trim();
        LobbyPlayer target = null;
        foreach (LobbyPlayer p in m_players)
        {
            if (string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase)) target = p;
        }

        if (target == null)
        {
            SetStatus("No hay ningun jugador llamado '" + name + "'");
            return false;
        }

        SendTo(target.conn, "KICK:");
        RemovePlayer(target, "ha sido expulsado");
        return true;
    }

    public void StartGame()
    {
        if (!m_running || m_gameStarted) return;

        if (!Application.CanStreamedLevelBeLoaded(gameSceneName))
        {
            SetStatus("La escena '" + gameSceneName + "' no esta en Build Settings");
            return;
        }

        m_gameStarted = true;
        LobbyData.PlayerNames = PlayerNames();
        LobbyData.LocalName = HostLabel;
        LobbyData.IsHost = true;

        Broadcast("START:");
        DontDestroyOnLoad(gameObject);     // el servidor sigue vivo en la escena de juego
        SceneManager.LoadScene(gameSceneName);
    }

    // ---------- Utilidades ----------
    string HostLabel { get { return LobbyNet.CleanName(serverName, 16) + " (Host)"; } }

    LobbyPlayer FindPlayer(object conn)
    {
        foreach (LobbyPlayer p in m_players)
        {
            if (p.conn.Equals(conn)) return p;
        }
        return null;
    }

    string UniqueName(string baseName)
    {
        string name = baseName;
        int n = 2;
        while (NameTaken(name))
        {
            name = baseName + " (" + n + ")";
            n++;
        }
        return name;
    }

    bool NameTaken(string name)
    {
        if (string.Equals(name, HostLabel, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (LobbyPlayer p in m_players)
        {
            if (string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    void AddChatLine(string line)
    {
        if (chatHistoryText == null) return;
        chatHistoryText.text += "\n" + line;
        if (chatScroll != null)
        {
            Canvas.ForceUpdateCanvases();
            chatScroll.verticalNormalizedPosition = 0f;
        }
    }

    void RefreshUI()
    {
        if (playersText == null) return;

        StringBuilder sb = new StringBuilder();
        sb.Append("Sala de espera (").Append(m_players.Count + 1).Append('/').Append(maxPlayers).Append("):\n");
        sb.Append(HostLabel);
        foreach (LobbyPlayer p in m_players)
        {
            sb.Append('\n').Append(p.name);
            if (p.pingMs >= 0) sb.Append(" - ").Append(p.pingMs).Append(" ms");
        }
        playersText.text = sb.ToString();
    }

    void SetStatus(string text)
    {
        m_status = text;
        if (statusText != null) statusText.text = text;
        Debug.Log(text);
    }

    // Si no asignas Status Text, el mensaje se ve igualmente en un recuadro (util en el .exe)
    void OnGUI()
    {
        if (statusText == null) LobbyGui.DrawStatus(m_status);
    }
}