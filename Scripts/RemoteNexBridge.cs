using UnityEngine;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class RemoteNexBridge : MonoBehaviour
{
    [Header("🎮 Simülatör Dosyaları")]
    public UnityEngine.Object simulatorHtmlFile;
    public UnityEngine.Object masterHtmlFile;
    public UnityEngine.Object normalHtmlFile;

    [Header("⚙️ Sunucu Ayarları")]
    public int port = 8080;
    public bool autoOpenBrowser = true;

    private HttpListener httpListener;
    private Thread serverThread;
    private bool isRunning = false;
    private string cachedSimPath, cachedMasterPath, cachedNormalPath, rootDirectory;

    private struct ServerMessage
    {
        public int id;
        public string content;
    }

    private List<ServerMessage> messageHistory = new List<ServerMessage>();
    private int globalMessageIdCounter = 0;
    private object lockObj = new object(); 

    private ConcurrentQueue<string> inboundQueue = new ConcurrentQueue<string>();
    private HashSet<string> connectedSimUsers = new HashSet<string>();

    void OnEnable()
    {
        RemoteNex.OnDataSent += BroadcastMessage;
        StartLocalServer();
    }

    void OnDisable()
    {
        RemoteNex.OnDataSent -= BroadcastMessage;
        StopLocalServer();
    }

    void BroadcastMessage(string data)
    {
        lock (lockObj)
        {
            globalMessageIdCounter++;
            messageHistory.Add(new ServerMessage { id = globalMessageIdCounter, content = data });

            if (messageHistory.Count > 500) messageHistory.RemoveAt(0);
        }
    }

    void Update()
    {
        while (inboundQueue.TryDequeue(out string rawData))
        {
            ProcessInboundData(rawData);
        }
    }

    void ProcessInboundData(string rawData)
    {
        string[] parts = rawData.Split(new string[] { "|||" }, StringSplitOptions.None);
        if (parts.Length < 2) return;

        string userId = parts[0];
        string content = parts[1];

        if (content == "EXIT")
        {
            if (connectedSimUsers.Contains(userId)) { connectedSimUsers.Remove(userId); SendPlayerList(); }
            return;
        }
        if (!connectedSimUsers.Contains(userId))
        {
            connectedSimUsers.Add(userId);
            SendPlayerList();
        }
        if (content == "JOIN") return;

        string gameMsg = content.StartsWith("MOVE") ? $"{userId}:{content}" : $"{userId}:MOVE:{content}";
        RemoteNex.TriggerInput(gameMsg);
    }

    void SendPlayerList()
    {
        List<string> list = new List<string>();
        int i = 0;
        foreach (var u in connectedSimUsers.OrderBy(x => x)) list.Add($"{i++}:SimUser:{u}");
        RemoteNex.TriggerInput($":PLAYERS:{string.Join(",", list)}");
    }

    void StartLocalServer()
    {
        if (simulatorHtmlFile == null) return;
#if UNITY_EDITOR
        cachedSimPath = Path.GetFullPath(AssetDatabase.GetAssetPath(simulatorHtmlFile));
        cachedMasterPath = (masterHtmlFile != null) ? Path.GetFullPath(AssetDatabase.GetAssetPath(masterHtmlFile)) : "";
        cachedNormalPath = (normalHtmlFile != null) ? Path.GetFullPath(AssetDatabase.GetAssetPath(normalHtmlFile)) : "";
        rootDirectory = (cachedMasterPath != "") ? Path.GetDirectoryName(cachedMasterPath) : "";
#endif
        try
        {
            httpListener = new HttpListener();
            httpListener.Prefixes.Add($"http://localhost:{port}/");
            httpListener.Start();
            isRunning = true;
            serverThread = new Thread(ServerLoop);
            serverThread.Start();
            if (autoOpenBrowser) Application.OpenURL($"http://localhost:{port}/");
        }
        catch (Exception e) { Debug.LogError("Port Hatası: " + e.Message); }
    }

    void StopLocalServer() { isRunning = false; if (httpListener != null) { httpListener.Stop(); httpListener.Close(); } if (serverThread != null) serverThread.Abort(); }

    void ServerLoop()
    {
        while (isRunning && httpListener != null && httpListener.IsListening)
        {
            try
            {
                var ctx = httpListener.GetContext();
                string path = ctx.Request.Url.AbsolutePath;

                if (ctx.Request.HttpMethod == "POST" && path == "/api/send") HandlePost(ctx);
                else if (ctx.Request.HttpMethod == "GET" && path == "/api/events") HandleGetEvents(ctx);
                else HandleGetFile(ctx);
            }
            catch { }
        }
    }

    void HandlePost(HttpListenerContext ctx)
    {
        using (var r = new StreamReader(ctx.Request.InputStream)) inboundQueue.Enqueue(r.ReadToEnd());
        ctx.Response.StatusCode = 200; ctx.Response.Close();
    }

    void HandleGetEvents(HttpListenerContext ctx)
    {
        int lastId = 0;
        string sinceParam = ctx.Request.QueryString["since"];
        if (!string.IsNullOrEmpty(sinceParam)) int.TryParse(sinceParam, out lastId);

        List<string> newMessages = new List<string>();
        int maxId = lastId;

        lock (lockObj)
        {
            foreach (var msg in messageHistory)
            {
                if (msg.id > lastId)
                {
                    newMessages.Add(msg.content);
                    maxId = Math.Max(maxId, msg.id);
                }
            }
        }

        string jsonArray = "[";
        for (int i = 0; i < newMessages.Count; i++)
        {
            jsonArray += "\"" + newMessages[i] + "\"";
            if (i < newMessages.Count - 1) jsonArray += ",";
        }
        jsonArray += "]";

        string responseJson = $"{{\"last_id\": {maxId}, \"messages\": {jsonArray}}}";

        byte[] buf = Encoding.UTF8.GetBytes(responseJson);
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = buf.Length;
        ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        ctx.Response.Close();
    }

    void HandleGetFile(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url.AbsolutePath;
        string file = (path == "/" || path == "/index.html") ? cachedSimPath :
                      (path == "/master") ? cachedMasterPath :
                      (path == "/normal") ? cachedNormalPath :
                      (rootDirectory != "") ? Path.Combine(rootDirectory, path.TrimStart('/')) : "";

        if (File.Exists(file)) ServeFile(ctx, file); else { ctx.Response.StatusCode = 404; ctx.Response.Close(); }
    }

    void ServeFile(HttpListenerContext ctx, string path)
    {
        byte[] buf = File.ReadAllBytes(path);
        string ext = Path.GetExtension(path).ToLower();
        if (path.Contains(".html.txt")) ext = ".html";

        if (ext == ".html")
        {
            string html = File.ReadAllText(path);
            string script = @"<script>
                const p = new URLSearchParams(window.location.search); const id = p.get('sim_id')||'U';
                window.ReactNativeWebView = { postMessage: function(m) { fetch('/api/send', {method:'POST', body:id+'|||'+m}); }};
                
                let lastMsgId = 0;
                setInterval(() => {
                    fetch('/api/events?since=' + lastMsgId)
                        .then(r => r.json())
                        .then(data => {
                            lastMsgId = data.last_id; // İmleci güncelle
                            if(data.messages.length > 0 && window.handleServerMessage) {
                                data.messages.forEach(m => { console.log('IN:', m); window.handleServerMessage(m); });
                            }
                        }).catch(e=>{});
                }, 100); // 100ms polling

                setTimeout(()=>{window.ReactNativeWebView.postMessage('JOIN')},500);
            </script></body>";

            if (html.Contains("</body>")) html = html.Replace("</body>", script); else html += script;
            buf = Encoding.UTF8.GetBytes(html);
        }

        ctx.Response.ContentType = (ext == ".html") ? "text/html" : "application/octet-stream";
        ctx.Response.ContentLength64 = buf.Length; ctx.Response.OutputStream.Write(buf, 0, buf.Length); ctx.Response.Close();
    }
}