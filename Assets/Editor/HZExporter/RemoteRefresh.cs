using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

/// <summary>
/// Listens on TCP port 7321 for commands from external processes.
/// Supported commands:
///   "refresh"  — triggers AssetDatabase.Refresh (compiles scripts)
///   "log"      — returns last compilation result
///   "ping"     — returns "pong"
/// </summary>
[InitializeOnLoad]
public static class RemoteRefresh
{
    const int Port = 7321;
    static TcpListener s_listener;
    static Thread s_thread;
    static volatile bool s_running;
    static volatile bool s_refreshRequested;

    // Persist compile results across domain reloads via temp file
    static readonly string s_resultFile = Path.Combine(Path.GetTempPath(), "unity_remote_refresh_result.txt");

    static RemoteRefresh()
    {
        // After domain reload from a successful compile, mark ok if still "compiling"
        string current = ReadResult();
        if (current == "compiling")
            WriteResult("ok");

        Start();
        EditorApplication.update += OnUpdate;
        CompilationPipeline.assemblyCompilationFinished += OnCompilationFinished;
    }

    static string ReadResult()
    {
        try { return File.Exists(s_resultFile) ? File.ReadAllText(s_resultFile).Trim() : "ok"; }
        catch { return "ok"; }
    }

    static void WriteResult(string result)
    {
        try { File.WriteAllText(s_resultFile, result); } catch { }
    }

    static void Start()
    {
        if (s_running) return;
        s_running = true;
        s_thread = new Thread(ListenLoop) { IsBackground = true, Name = "RemoteRefresh" };
        s_thread.Start();
        Debug.Log($"[RemoteRefresh] Listening on port {Port}");
    }

    static void ListenLoop()
    {
        try
        {
            s_listener = new TcpListener(IPAddress.Loopback, Port);
            s_listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s_listener.Start();

            while (s_running)
            {
                if (!s_listener.Pending())
                {
                    Thread.Sleep(50);
                    continue;
                }

                using var client = s_listener.AcceptTcpClient();
                client.ReceiveTimeout = 2000;
                using var stream = client.GetStream();

                var buf = new byte[256];
                int n = stream.Read(buf, 0, buf.Length);
                string cmd = Encoding.UTF8.GetString(buf, 0, n).Trim().ToLowerInvariant();

                string response;
                switch (cmd)
                {
                    case "refresh":
                        WriteResult("compiling");
                        s_refreshRequested = true;
                        response = "refresh_queued";
                        break;
                    case "log":
                        response = ReadResult();
                        break;
                    case "ping":
                        response = "pong";
                        break;
                    default:
                        response = $"unknown_command:{cmd}";
                        break;
                }

                byte[] resp = Encoding.UTF8.GetBytes(response);
                stream.Write(resp, 0, resp.Length);
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            Debug.LogWarning($"[RemoteRefresh] Port {Port} already in use, skipping.");
        }
        catch (Exception ex)
        {
            if (s_running) Debug.LogError($"[RemoteRefresh] {ex.Message}");
        }
    }

    static void OnUpdate()
    {
        if (s_refreshRequested)
        {
            s_refreshRequested = false;
            Debug.Log("[RemoteRefresh] Triggering AssetDatabase.Refresh...");
            AssetDatabase.Refresh(ImportAssetOptions.Default);
        }
    }

    static void OnCompilationFinished(string assemblyPath, CompilerMessage[] messages)
    {
        var sb = new StringBuilder();
        int errors = 0;
        foreach (var msg in messages)
        {
            if (msg.type == CompilerMessageType.Error)
            {
                errors++;
                sb.AppendLine(msg.message);
            }
        }
        // Write errors to file — survives domain reload
        if (errors > 0)
            WriteResult($"ERRORS({errors}):\n{sb}");
        // Don't write "ok" here — domain reload will do that
    }
}
