using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using TMPro;

public class SocketsUDPServer : MonoBehaviour
{
    public int port = 9050;
    public string serverName = "Host";
    public TextMeshProUGUI playersText;
    public TextMeshProUGUI chatHistoryText;
    public TMP_InputField inputChat;

    const int MaxPacketSize = 64 * 1024;
    const float TimeoutSeconds = 5.0f;

    struct Packet { public byte[] data; public EndPoint from; }
    struct ClientInfo { public string name; public float lastSeen; }

    Socket m_socket;
    readonly Dictionary<EndPoint, ClientInfo> m_clients = new Dictionary<EndPoint, ClientInfo>();
    readonly List<Thread> m_threads = new List<Thread>();
    readonly ConcurrentQueue<Packet> m_inbox = new ConcurrentQueue<Packet>();
    volatile bool m_running;

    void Start()
    {
        Application.runInBackground = true;
        StartNetwork();
    }

    public void StartNetwork()
    {
        if (m_running) return;
        m_running = true;
        StartThread(ServerThread);
    }

    public void Disconnect()
    {
        if (!m_running) return;
        m_running = false;

        if (m_socket != null) { try { m_socket.Close(); } catch { } m_socket = null; }

        lock (m_clients) m_clients.Clear();

        Thread[] threads;
        lock (m_threads) { threads = m_threads.ToArray(); m_threads.Clear(); }
        foreach (Thread t in threads) if (t != Thread.CurrentThread) t.Join(500);
    }

    void OnDestroy() { Disconnect(); }

    void Update()
    {
        Packet packet;
        while (m_inbox.TryDequeue(out packet))
            OnPacketReceived(packet.data, packet.from);

        CheckTimeouts();
    }

    void CheckTimeouts()
    {
        float now = Time.time;
        bool changed = false;

        lock (m_clients)
        {
            List<EndPoint> toRemove = new List<EndPoint>();
            foreach (var kvp in m_clients)
            {
                if (now - kvp.Value.lastSeen > TimeoutSeconds)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (EndPoint ep in toRemove)
            {
                m_clients.Remove(ep);
                changed = true;
            }
        }

        if (changed) BroadcastPlayerList();
    }

    void ServerThread()
    {
        try
        {
            m_socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            m_socket.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch (SocketException) { return; }

        ReceiveLoop();
    }

    void ReceiveLoop()
    {
        byte[] buffer = new byte[MaxPacketSize];

        while (m_running)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int received;
            try { received = m_socket.ReceiveFrom(buffer, ref from); }
            catch (Exception) { break; }

            if (received <= 0) continue;

            byte[] payload = new byte[received];
            Array.Copy(buffer, payload, received);

            m_inbox.Enqueue(new Packet { data = payload, from = from });
        }
    }

    void OnPacketReceived(byte[] data, EndPoint from)
    {
        string text = Encoding.UTF8.GetString(data);
        float now = Time.time;

        lock (m_clients)
        {
            if (text.StartsWith("JOIN:"))
            {
                string name = text.Substring(5);
                m_clients[from] = new ClientInfo { name = name, lastSeen = now };
                BroadcastPlayerList();
                return;
            }

            if (m_clients.ContainsKey(from))
            {
                ClientInfo info = m_clients[from];
                info.lastSeen = now;
                m_clients[from] = info;
            }
            else
            {
                return; // Ignorar paquetes de clientes no registrados
            }

            if (text.StartsWith("PING:"))
            {
                return;
            }
            else if (text.StartsWith("LEAVE:"))
            {
                m_clients.Remove(from);
                BroadcastPlayerList();
            }
            else if (text.StartsWith("CHAT:"))
            {
                string msg = text.Substring(5);
                string senderName = m_clients[from].name;
                string fullMsg = senderName + ": " + msg;

                if (chatHistoryText != null)
                    chatHistoryText.text += "\n" + fullMsg;

                BroadcastString("CHAT:" + fullMsg);
            }
        }
    }

    public void SendChatMessage()
    {
        if (inputChat == null || string.IsNullOrEmpty(inputChat.text)) return;

        string fullMsg = serverName + " (Host): " + inputChat.text;
        if (chatHistoryText != null)
            chatHistoryText.text += "\n" + fullMsg;

        BroadcastString("CHAT:" + fullMsg);
        inputChat.text = "";
    }

    void BroadcastPlayerList()
    {
        List<string> names = new List<string>();
        names.Add(serverName + " (Host)");

        foreach (var kvp in m_clients)
        {
            names.Add(kvp.Value.name);
        }

        string listMsg = "PLAYERS:" + string.Join(", ", names);

        if (playersText != null)
        {
            playersText.text = "Sala de espera:\n" + string.Join("\n", names);
        }

        BroadcastString(listMsg);
    }

    void BroadcastString(string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        List<EndPoint> targets = new List<EndPoint>();

        lock (m_clients) { targets.AddRange(m_clients.Keys); }

        foreach (EndPoint ep in targets)
        {
            try { m_socket.SendTo(payload, ep); } catch { }
        }
    }

    void StartThread(ThreadStart work)
    {
        Thread t = new Thread(work);
        t.IsBackground = true;
        lock (m_threads) m_threads.Add(t);
        t.Start();
    }
}