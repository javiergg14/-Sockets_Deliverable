using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// Datos que sobreviven al cambio de escena (sala -> partida, partida -> menu).
public static class LobbyData
{
    public static string[] PlayerNames = new string[0];
    public static string LocalName = "";
    public static bool IsHost = false;
    public static string LastMessage = "";   // motivo por el que volvemos al menu (expulsado, sala cerrada...)
}

public enum NetEventType { Opened, Data, Closed }

// Lo unico que los hilos de red dejan en la cola. Todo lo demas ocurre en el hilo principal.
public class NetEvent
{
    public NetEventType type;
    public object conn;   // Socket (TCP) o EndPoint (UDP)
    public string text;
    public long ms;       // momento en que llego, medido en el hilo de red
}

public class LobbyPlayer
{
    public string name;
    public object conn;
    public long lastSeenMs;
    public int pingMs = -1;
}

public static class LobbyNet
{
    public const int MaxPacketSize = 64 * 1024;

    static readonly System.Diagnostics.Stopwatch s_clock = System.Diagnostics.Stopwatch.StartNew();
    public static long NowMs { get { return s_clock.ElapsedMilliseconds; } }

    public static Thread StartThread(ThreadStart work)
    {
        Thread t = new Thread(work);
        t.IsBackground = true;
        t.Start();
        return t;
    }

    // TCP: 4 bytes con la longitud + el texto UTF-8
    public static byte[] Frame(string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        byte[] framed = new byte[4 + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(framed, 0);
        payload.CopyTo(framed, 4);
        return framed;
    }

    public static bool ReadExactly(Socket socket, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read;
            try { read = socket.Receive(buffer, total, count - total, SocketFlags.None); }
            catch (Exception) { return false; }

            if (read == 0) return false;   // el otro lado ha cerrado
            total += read;
        }
        return true;
    }

    public static bool ReadFramed(Socket socket, out string text)
    {
        text = null;
        byte[] header = new byte[4];
        if (!ReadExactly(socket, header, 4)) return false;

        int size = BitConverter.ToInt32(header, 0);
        if (size <= 0 || size > MaxPacketSize) return false;

        byte[] payload = new byte[size];
        if (!ReadExactly(socket, payload, size)) return false;

        text = Encoding.UTF8.GetString(payload);
        return true;
    }

    public static void CloseSocket(Socket socket)
    {
        if (socket == null) return;
        try { socket.Shutdown(SocketShutdown.Both); } catch (Exception) { }
        try { socket.Close(); } catch (Exception) { }
    }

    // Windows: si envias UDP a un puerto cerrado, el SIGUIENTE ReceiveFrom lanza
    // "ConnectionReset" (10054) y el bucle de recepcion moria. Esto lo desactiva.
    public static void DisableUdpConnReset(Socket socket)
    {
        try { socket.IOControl((IOControlCode)(-1744830452), new byte[] { 0, 0, 0, 0 }, null); }
        catch (Exception) { }
    }

    // Quita lo que rompe el protocolo de texto (comas, '=', saltos de linea) y limita la longitud.
    public static string CleanName(string raw, int maxLen)
    {
        if (raw == null) raw = "";
        StringBuilder sb = new StringBuilder();
        foreach (char ch in raw.Trim())
        {
            if (ch == ',' || ch == '=' || char.IsControl(ch)) continue;
            sb.Append(ch);
            if (sb.Length >= maxLen) break;
        }
        string result = sb.ToString().Trim();
        return result.Length == 0 ? "Player" : result;
    }

    public static string LocalIps()
    {
        try
        {
            List<string> ips = new List<string>();
            foreach (IPAddress a in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
            {
                if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    ips.Add(a.ToString());
            }
            return ips.Count > 0 ? string.Join(", ", ips.ToArray()) : "?";
        }
        catch (Exception) { return "?"; }
    }
}

// Mensajes de estado en pantalla cuando no hay un TextMeshPro asignado (asi se ven tambien en el .exe).
public static class LobbyGui
{
    static GUIStyle s_box;

    public static void DrawStatus(string status)
    {
        if (string.IsNullOrEmpty(status)) return;
        if (s_box == null)
        {
            s_box = new GUIStyle(GUI.skin.box);
            s_box.wordWrap = true;
            s_box.alignment = TextAnchor.UpperLeft;
            s_box.fontSize = 16;
            s_box.normal.textColor = Color.white;
        }
        GUI.Box(new Rect(10, 10, Screen.width - 20, 70), status, s_box);
    }
}
