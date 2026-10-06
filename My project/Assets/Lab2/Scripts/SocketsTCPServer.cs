using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using TMPro;

public class SocketsTCPServer : MonoBehaviour
{
    public TextMeshProUGUI playersText;
    public TMP_InputField inputChat;
    public TextMeshProUGUI chatHistoryText;
    public int port = 9050;
    public string serverName = "Host";

    const int MaxPacketSize = 64 * 1024;
    struct Packet { public byte[] data; public Socket from; }

    Socket m_listener;
    readonly List<Socket> m_clients = new List<Socket>();
    readonly Dictionary<Socket, string> m_playerNames = new Dictionary<Socket, string>();
    readonly List<Thread> m_threads = new List<Thread>();
    readonly ConcurrentQueue<Packet> m_inbox = new ConcurrentQueue<Packet>();
    volatile bool m_running;

    string m_pendingPlayersText = "";
    bool m_updatePlayersUI = false;

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
        UpdateLocalPlayerList();
    }

    public void SendChatMessage()
    {
        if (inputChat == null || string.IsNullOrEmpty(inputChat.text)) return;

        string messageContent = serverName + " (Host): " + inputChat.text;

        if (chatHistoryText != null)
        {
            chatHistoryText.text += "\n" + messageContent;
        }

        BroadcastString("CHAT:" + messageContent);
        inputChat.text = "";
    }

    public void Disconnect()
    {
        if (!m_running) return;
        m_running = false;

        Socket[] clients;
        lock (m_clients) { clients = m_clients.ToArray(); m_clients.Clear(); }
        foreach (Socket c in clients) CloseSocket(c);

        CloseSocket(m_listener); m_listener = null;

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

        if (m_updatePlayersUI && playersText != null)
        {
            playersText.text = m_pendingPlayersText;
            m_updatePlayersUI = false;
        }
    }

    void ServerThread()
    {
        try
        {
            m_listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            m_listener.Bind(new IPEndPoint(IPAddress.Any, port));
            m_listener.Listen(10);
        }
        catch (SocketException) { return; }

        while (m_running)
        {
            Socket client;
            try { client = m_listener.Accept(); }
            catch (Exception) { break; }

            lock (m_clients) m_clients.Add(client);
            Socket captured = client;
            StartThread(delegate { ClientThread(captured); });
        }
    }

    void ClientThread(Socket client)
    {
        ReceiveLoop(client);

        lock (m_clients)
        {
            m_clients.Remove(client);
            m_playerNames.Remove(client);
        }
        CloseSocket(client);
        BroadcastPlayerList();
    }

    void ReceiveLoop(Socket socket)
    {
        byte[] header = new byte[4];
        while (m_running)
        {
            if (!ReadExactly(socket, header, 4)) return;

            int size = BitConverter.ToInt32(header, 0);
            if (size <= 0 || size > MaxPacketSize) return;

            byte[] payload = new byte[size];
            if (!ReadExactly(socket, payload, size)) return;

            m_inbox.Enqueue(new Packet { data = payload, from = socket });
        }
    }

    bool ReadExactly(Socket socket, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read;
            try { read = socket.Receive(buffer, total, count - total, SocketFlags.None); }
            catch (Exception) { return false; }

            if (read == 0) return false;
            total += read;
        }
        return true;
    }

    void OnPacketReceived(byte[] data, Socket from)
    {
        string text = Encoding.UTF8.GetString(data);

        if (text.StartsWith("JOIN:"))
        {
            string playerName = text.Substring(5);
            lock (m_clients)
            {
                m_playerNames[from] = playerName;
            }
            BroadcastPlayerList();
        }
        else if (text.StartsWith("CHAT:"))
        {
            string msg = text.Substring(5);
            string senderName = "Desconocido";
            lock (m_clients)
            {
                if (m_playerNames.ContainsKey(from)) senderName = m_playerNames[from];
            }

            string fullMessage = senderName + ": " + msg;

            if (chatHistoryText != null)
            {
                chatHistoryText.text += "\n" + fullMessage;
            }

            BroadcastString("CHAT:" + fullMessage);
        }
    }

    void BroadcastPlayerList()
    {
        string listMsg = "PLAYERS:";
        lock (m_clients)
        {
            List<string> names = new List<string>(m_playerNames.Values);
            names.Insert(0, serverName + " (Host)");

            listMsg += string.Join(", ", m_playerNames.Values);

            m_pendingPlayersText = "Sala de espera:\n" + string.Join("\n", names);
            m_updatePlayersUI = true;
        }

        BroadcastString(listMsg);
    }

    void UpdateLocalPlayerList()
    {
        lock (m_clients)
        {
            List<string> names = new List<string>(m_playerNames.Values);
            names.Insert(0, serverName + " (Host)");
            m_pendingPlayersText = "Sala de espera:\n" + string.Join("\n", names);
            m_updatePlayersUI = true;
        }
    }

    void BroadcastString(string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        byte[] framed = new byte[4 + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(framed, 0);
        payload.CopyTo(framed, 4);

        Socket[] targets;
        lock (m_clients) targets = m_clients.ToArray();

        foreach (Socket c in targets)
        {
            try { c.Send(framed); } catch { }
        }
    }

    void StartThread(ThreadStart work)
    {
        Thread t = new Thread(work);
        t.IsBackground = true;
        lock (m_threads) m_threads.Add(t);
        t.Start();
    }

    void CloseSocket(Socket socket)
    {
        if (socket == null) return;
        try { socket.Shutdown(SocketShutdown.Both); } catch { }
        try { socket.Close(); } catch { }
    }
}