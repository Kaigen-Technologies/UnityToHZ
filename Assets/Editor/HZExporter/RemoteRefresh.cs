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
    static volatile string s_execMethod;

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
                string rawCmd = Encoding.UTF8.GetString(buf, 0, n).Trim();
                string cmd = rawCmd.ToLowerInvariant();

                string response;
                if (cmd == "refresh")
                {
                    WriteResult("compiling");
                    s_refreshRequested = true;
                    response = "refresh_queued";
                }
                else if (cmd == "log")
                {
                    response = ReadResult();
                }
                else if (cmd == "ping")
                {
                    response = "pong";
                }
                else if (cmd.StartsWith("exec:"))
                {
                    // exec:ClassName.MethodName — runs a static method on main thread
                    s_execMethod = rawCmd.Substring(5); // preserve case
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
