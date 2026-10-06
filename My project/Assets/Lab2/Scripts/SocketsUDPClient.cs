using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using TMPro;

public class SocketsUDPClient : MonoBehaviour
{
    public TMP_InputField inputName;
    public TMP_InputField inputIP;
    public TMP_InputField inputChat;
    public TextMeshProUGUI playersListText;
    public TextMeshProUGUI chatHistoryText;
    public int port = 9050;

    const int MaxPacketSize = 64 * 1024;

    Socket m_socket;
    EndPoint m_serverEndPoint;
    readonly List<Thread> m_threads = new List<Thread>();
    readonly ConcurrentQueue<byte[]> m_inbox = new ConcurrentQueue<byte[]>();
    volatile bool m_running;
    string m_playerName;

    void Start()
    {
        Application.runInBackground = true;
    }

    public void ConnectToServer()
    {
        if (m_running || string.IsNullOrEmpty(inputIP.text) || string.IsNullOrEmpty(inputName.text)) return;

        m_playerName = inputName.text;
        try
        {
            m_socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            m_serverEndPoint = new IPEndPoint(IPAddress.Parse(inputIP.text), port);
        }
        catch (Exception) { return; }

        m_running = true;
        StartThread(ReceiveLoop);
        StartThread(PingLoop);

        SendString("JOIN:" + m_playerName);
    }

    public void SendChatMessage()
    {
        if (inputChat == null || string.IsNullOrEmpty(inputChat.text)) return;

        SendString("CHAT:" + inputChat.text);
        inputChat.text = "";
    }

    public void SendString(string text)
    {
        if (m_socket == null || m_serverEndPoint == null) return;
        byte[] payload = Encoding.UTF8.GetBytes(text);
        try { m_socket.SendTo(payload, m_serverEndPoint); } catch { }
    }

    void Update()
    {
        byte[] data;
        while (m_inbox.TryDequeue(out data))
            OnPacketReceived(data);
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
            m_inbox.Enqueue(payload);
        }
    }

    void PingLoop()
    {
        while (m_running)
        {
            SendString("PING:");
            Thread.Sleep(1000); // Enviar ping cada 1 segundo
        }
    }

    void OnPacketReceived(byte[] data)
    {
        string message = Encoding.UTF8.GetString(data);

        if (message.StartsWith("PLAYERS:"))
        {
            string playersStr = message.Substring(8);
            if (playersListText != null)
            {
                playersListText.text = "Jugadores en sala:\n" + playersStr.Replace(", ", "\n");
            }
        }
        else if (message.StartsWith("CHAT:"))
        {
            string chatMsg = message.Substring(5);
            if (chatHistoryText != null)
            {
                chatHistoryText.text += "\n" + chatMsg;
            }
        }
    }

    public void Disconnect()
    {
        if (!m_running) return;
        SendString("LEAVE:");
        m_running = false;

        if (m_socket != null) { try { m_socket.Close(); } catch { } m_socket = null; }

        Thread[] threads;
        lock (m_threads) { threads = m_threads.ToArray(); m_threads.Clear(); }
        foreach (Thread t in threads) if (t != Thread.CurrentThread) t.Join(500);
    }

    void OnDestroy() { Disconnect(); }

    void StartThread(ThreadStart work)
    {
        Thread t = new Thread(work);
        t.IsBackground = true;
        lock (m_threads) m_threads.Add(t);
        t.Start();
    }
}