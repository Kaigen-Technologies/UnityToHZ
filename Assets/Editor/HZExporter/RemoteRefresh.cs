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
///   "exec:Type.Method" — runs a static method on the main thread
/// </summary>
[InitializeOnLoad]
public static class RemoteRefresh
{
    const int Port = 7321;
    static TcpListener s_listener;
    static Thread s_thread;
    static volatile bool s_running;
    static volatile bool s_refreshRequested;
    static volatile string s_execMethod;

    // In-memory compile result. After domain reload, the static constructor
    // runs again and resets this to "ok" (meaning compile succeeded).
    static volatile string s_compileResult = "ok";

    static RemoteRefresh()
    {
        // Domain reload happened — if we get here, compilation succeeded.
        s_compileResult = "ok";
        Start();
        EditorApplication.update += OnUpdate;
        CompilationPipeline.assemblyCompilationFinished += OnCompilationFinished;
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
                string rawCmd = Encoding.UTF8.GetString(buf, 0, n).Trim();
                string cmd = rawCmd.ToLowerInvariant();

                string response;
                if (cmd == "refresh")
                {
                    s_compileResult = "compiling";
                    s_refreshRequested = true;
                    response = "refresh_queued";
                }
                else if (cmd == "log")
                {
                    response = s_compileResult;
                }
                else if (cmd == "ping")
                {
                    response = "pong";
                }
                else if (cmd.StartsWith("exec:"))
                {
                    s_execMethod = rawCmd.Substring(5);
                    response = $"exec_queued:{s_execMethod}";
                }
                else
                {
                    response = $"unknown_command:{cmd}";
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
            // If Refresh returns without triggering compilation (no changes),
            // we're still here (no domain reload), so mark as ok.
            // If compilation IS triggered, domain reload will destroy this instance
            // and the new static constructor sets s_compileResult = "ok".
            if (s_compileResult == "compiling")
                s_compileResult = "ok";
        }

        if (s_execMethod != null)
        {
            string method = s_execMethod;
            s_execMethod = null;
            try
            {
                int dot = method.LastIndexOf('.');
                if (dot < 0) { Debug.LogError($"[RemoteRefresh] Invalid method: {method}"); return; }
                string typeName = method.Substring(0, dot);
                string methodName = method.Substring(dot + 1);

                System.Type type = null;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = asm.GetType(typeName);
                    if (type != null) break;
                }
                if (type == null) { Debug.LogError($"[RemoteRefresh] Type not found: {typeName}"); return; }

                var mi = type.GetMethod(methodName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (mi == null) { Debug.LogError($"[RemoteRefresh] Method not found: {method}"); return; }

                Debug.Log($"[RemoteRefresh] Executing {method}...");
                mi.Invoke(null, null);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[RemoteRefresh] exec failed: {ex}");
            }
        }
    }

    static void OnCompilationFinished(string assemblyPath, CompilerMessage[] messages)
    {
        int errors = 0;
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            if (msg.type == CompilerMessageType.Error)
            {
                errors++;
                sb.AppendLine(msg.message);
            }
        }
        if (errors > 0)
            s_compileResult = $"ERRORS({errors}):\n{sb}";
        // "ok" is set by the static constructor after domain reload
    }
}
