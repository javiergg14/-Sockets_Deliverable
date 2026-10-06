using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using TMPro;

public class SocketsClientRoom : MonoBehaviour
{
    public TMP_InputField inputChat;
    public TextMeshProUGUI chatHistoryText;
    public TextMeshProUGUI playersListText;
    public int port = 9050;

    public static Socket s_activeConnection;
    public static string s_playerName;
    public static string s_serverIp;

    const int MaxPacketSize = 64 * 1024;
    readonly List<Thread> m_threads = new List<Thread>();
    readonly ConcurrentQueue<byte[]> m_inbox = new ConcurrentQueue<byte[]>();
    volatile bool m_running;

    void Start()
    {
        Application.runInBackground = true;
        m_running = true;

        if (s_activeConnection != null && s_activeConnection.Connected)
        {
            StartThread(ReceiveLoop);
        }
    }

    public void SendChatMessage()
    {
        if (inputChat == null || string.IsNullOrEmpty(inputChat.text)) return;
        SendString("CHAT:" + inputChat.text);
        inputChat.text = "";
    }

    public void SendString(string text)
    {
        if (s_activeConnection == null || !s_activeConnection.Connected) return;

        byte[] payload = Encoding.UTF8.GetBytes(text);
        byte[] framed = new byte[4 + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(framed, 0);
        payload.CopyTo(framed, 4);

        try { s_activeConnection.Send(framed); }
        catch (Exception) { }
    }

    void Update()
    {
        byte[] data;
        while (m_inbox.TryDequeue(out data))
            OnPacketReceived(data);
    }

    void ReceiveLoop()
    {
        byte[] header = new byte[4];
        while (m_running && s_activeConnection != null)
        {
            if (!ReadExactly(s_activeConnection, header, 4)) return;

            int size = BitConverter.ToInt32(header, 0);
            if (size <= 0 || size > MaxPacketSize) return;

            byte[] payload = new byte[size];
            if (!ReadExactly(s_activeConnection, payload, size)) return;

            m_inbox.Enqueue(payload);
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

    void OnPacketReceived(byte[] data)
    {
        string message = Encoding.UTF8.GetString(data);

        if (message.StartsWith("PLAYERS:"))
        {
            string playersStr = message.Substring(8);
            if (playersListText != null)
            {
                playersListText.text = "Jugadores:\n" + playersStr.Replace(", ", "\n");
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

    void StartThread(ThreadStart work)
    {
        Thread t = new Thread(work);
        t.IsBackground = true;
        lock (m_threads) m_threads.Add(t);
        t.Start();
    }

    void OnDestroy()
    {
        m_running = false;
        Thread[] threads;
        lock (m_threads) { threads = m_threads.ToArray(); m_threads.Clear(); }
        foreach (Thread t in threads) if (t != Thread.CurrentThread) t.Join(500);
    }
}