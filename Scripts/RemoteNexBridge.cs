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

    private string cachedSimPath;
    private string cachedMasterPath;
    private string cachedNormalPath;
    private string rootDirectory;

    private const string FAKE_MOVE_KEY = "MOVE";
    private ConcurrentQueue<string> messageQueue = new ConcurrentQueue<string>();

    // Bağlı kullanıcıları tutan liste
    private HashSet<string> connectedSimUsers = new HashSet<string>();

    void Start()
    {
        StartLocalServer();
    }

    void Update()
    {
        while (messageQueue.TryDequeue(out string rawData))
        {
            // Format: "SimPlayer_1|||DATA"
            string[] parts = rawData.Split(new string[] { "|||" }, StringSplitOptions.None);
            if (parts.Length < 2) continue;

            string userId = parts[0];
            string content = parts[1];

            // --- ÇIKIŞ İŞLEMİ (EXIT) ---
            if (content == "EXIT")
            {
                if (connectedSimUsers.Contains(userId))
                {
                    connectedSimUsers.Remove(userId);
                    Debug.Log($"❌ [SİMÜLATÖR] Ayrıldı: {userId}");

                    // 🔥 KRİTİK: Listeden sildikten sonra oyuna GÜNCEL listeyi hemen yolla.
                    // Oyun, listede bu ID'yi göremeyince objeyi yok edecek.
                    SendFullPlayerListToGame();
                }
                continue;
            }

            // --- GİRİŞ İŞLEMİ (JOIN / MOVE) ---
            // Eğer listede yoksa ekle ve listeyi güncelle
            if (!connectedSimUsers.Contains(userId))
            {
                connectedSimUsers.Add(userId);
                SendFullPlayerListToGame();
            }

            // JOIN sadece listeye girmek içindi, işi bitti.
            if (content == "JOIN") continue;

            // --- HAREKET VERİSİ ---
            if (content.StartsWith("MOVE"))
            {
                string dataPart = content.Substring(5); // "UP:PRESS"
                string gameMsg = $"{userId}:{FAKE_MOVE_KEY}:{dataPart}";

                // Debug.Log($"📩 [SİMÜLATÖR] Hareket: {gameMsg}");
                RemoteNex.TriggerInput(gameMsg);
            }
        }
    }

    // Listeyi oyuna gönderen fonksiyon
    void SendFullPlayerListToGame()
    {
        // Liste boşsa bile gönder ki oyun herkesin çıktığını anlasın.

        List<string> formattedUsers = new List<string>();
        int index = 0;
        var sortedUsers = connectedSimUsers.OrderBy(u => u).ToList();

        foreach (var user in sortedUsers)
        {
            // Backend formatı: INDEX:NAME:ID
            formattedUsers.Add($"{index}:SimUser:{user}");
            index++;
        }

        string fullListString = string.Join(",", formattedUsers);
        // Format: :PLAYERS:User1,User2...
        string finalPacket = $":PLAYERS:{fullListString}";

        Debug.Log($"📋 [SDK -> OYUN] Oyuncu Listesi: {finalPacket}");
        RemoteNex.TriggerInput(finalPacket);
    }

    // --- SUNUCU KODLARININ GERİ KALANI (AYNEN KALIYOR) ---
    void OnApplicationQuit() { StopLocalServer(); }
    void StartLocalServer()
    {
        if (simulatorHtmlFile == null) return;
#if UNITY_EDITOR
        cachedSimPath = Path.GetFullPath(AssetDatabase.GetAssetPath(simulatorHtmlFile));
        cachedMasterPath = (masterHtmlFile != null) ? Path.GetFullPath(AssetDatabase.GetAssetPath(masterHtmlFile)) : "";
        cachedNormalPath = (normalHtmlFile != null) ? Path.GetFullPath(AssetDatabase.GetAssetPath(normalHtmlFile)) : "";
        rootDirectory = (cachedMasterPath != "") ? Path.GetDirectoryName(cachedMasterPath) : "";
#endif
        try { httpListener = new HttpListener(); httpListener.Prefixes.Add($"http://localhost:{port}/"); httpListener.Start(); isRunning = true; serverThread = new Thread(ServerLoop); serverThread.Start(); if (autoOpenBrowser) Application.OpenURL($"http://localhost:{port}/"); } catch { }
    }
    void StopLocalServer() { isRunning = false; if (httpListener != null) { httpListener.Stop(); httpListener.Close(); } if (serverThread != null) serverThread.Abort(); }
    void ServerLoop() { while (isRunning && httpListener != null && httpListener.IsListening) { try { var ctx = httpListener.GetContext(); if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url.AbsolutePath == "/api/send") HandlePost(ctx); else HandleGet(ctx); } catch { } } }
    void HandlePost(HttpListenerContext ctx) { using (var r = new StreamReader(ctx.Request.InputStream)) messageQueue.Enqueue(r.ReadToEnd()); ctx.Response.Close(); }
    void HandleGet(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url.AbsolutePath;
        if (path.Contains("favicon")) { ctx.Response.StatusCode = 200; ctx.Response.Close(); return; }
        string file = "";
        if (path == "/" || path == "/index.html") file = cachedSimPath;
        else if (path == "/master") file = cachedMasterPath;
        else if (path == "/normal") file = cachedNormalPath;
        else if (rootDirectory != "") file = Path.Combine(rootDirectory, path.TrimStart('/'));

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
                window.ReactNativeWebView={postMessage:function(m){fetch('/api/send',{method:'POST',body:id+'|||'+m})}};
                setTimeout(()=>{window.ReactNativeWebView.postMessage('JOIN')},500);
                function t(e,ty){e.dispatchEvent(new Event(ty,{bubbles:true,cancelable:true}))}
                document.addEventListener('mousedown',e=>t(e.target,'touchstart'));
                document.addEventListener('mouseup',e=>t(e.target,'touchend'));
            </script></body>";
            if (html.Contains("</body>")) html = html.Replace("</body>", script); else html += script;
            buf = Encoding.UTF8.GetBytes(html);
        }
        ctx.Response.ContentType = (ext == ".html") ? "text/html" : (ext == ".css" ? "text/css" : "application/octet-stream");
        ctx.Response.ContentLength64 = buf.Length; ctx.Response.OutputStream.Write(buf, 0, buf.Length); ctx.Response.Close();
    }
}