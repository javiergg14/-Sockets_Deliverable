using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SocketsTCPClient : MonoBehaviour
{
    public TMP_InputField inputName;
    public TMP_InputField inputIP;
    public int port = 9050;

    private string serverIp;
    const int MaxPacketSize = 64 * 1024;

    Socket m_connection;
    readonly List<Thread> m_threads = new List<Thread>();
    readonly ConcurrentQueue<byte[]> m_inbox = new ConcurrentQueue<byte[]>();
    volatile bool m_running;

    void Start()
    {
        Application.runInBackground = true;
    }

    public void ConnectToServer()
    {
        if (m_running || string.IsNullOrEmpty(inputIP.text) || string.IsNullOrEmpty(inputName.text)) return;

        serverIp = inputIP.text;
        try
        {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(new IPEndPoint(IPAddress.Parse(serverIp), port));

            // Guardamos los datos estáticos para la sala
            SocketsClientRoom.s_activeConnection = socket;
            SocketsClientRoom.s_playerName = inputName.text;
            SocketsClientRoom.s_serverIp = serverIp;

            // Enviamos el JOIN inicial antes de cambiar de escena
            byte[] payload = Encoding.UTF8.GetBytes("JOIN:" + inputName.text);
            byte[] framed = new byte[4 + payload.Length];
            BitConverter.GetBytes(payload.Length).CopyTo(framed, 0);
            payload.CopyTo(framed, 4);
            socket.Send(framed);

            // Cargamos la escena de la sala
            SceneManager.LoadScene("S_Room_TCP");
        }
        catch (Exception)
        {
            Debug.Log("No se pudo conectar al servidor.");
        }
    }

    public void Disconnect()
    {
        if (!m_running) return;
        m_running = false;

        CloseSocket(m_connection);
        m_connection = null;

        Thread[] threads;
        lock (m_threads) { threads = m_threads.ToArray(); m_threads.Clear(); }
        foreach (Thread t in threads) if (t != Thread.CurrentThread) t.Join(500);
    }

    void OnDestroy() { Disconnect(); }

    void Update()
    {
        byte[] data;
        while (m_inbox.TryDequeue(out data))
            OnPacketReceived(data);
    }

    void ClientThread()
    {
        try { m_connection = StartClient(); }
        catch (SocketException) { m_running = false; return; }

        if (m_connection == null) return;

        OnConnected();
        ReceiveLoop(m_connection);
    }

    Socket StartClient()
    {
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(new IPEndPoint(IPAddress.Parse(serverIp), port));
        return socket;
    }

    void OnConnected()
    {
        SendString("JOIN:" + inputName.text);
    }

    public void SendString(string text)
    {
        if (m_connection == null) return;

        byte[] payload = Encoding.UTF8.GetBytes(text);
        byte[] framed = new byte[4 + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(framed, 0);
        payload.CopyTo(framed, 4);

        try { m_connection.Send(framed); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
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
        // Aquí puedes gestionar si en el futuro quieres que el cliente cambie de escena al conectarse
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