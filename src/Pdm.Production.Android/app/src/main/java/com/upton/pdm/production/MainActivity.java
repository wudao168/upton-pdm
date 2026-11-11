package com.upton.pdm.production;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Bundle;
import android.webkit.*;
import android.net.Uri;
import org.json.JSONObject;
import java.net.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.*;

public final class MainActivity extends Activity {
    private static final String ORIGIN = "https://appassets.androidplatform.net";
    private WebView web;
    private final ExecutorService requests = Executors.newFixedThreadPool(3);
    private volatile String accessToken = "";
    private volatile String server = "";

    @Override public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().setStatusBarColor(0xff23804b);
        getWindow().setNavigationBarColor(0xfff3f7f3);
        server = getPreferences(MODE_PRIVATE).getString("server", "");
        web = new WebView(this);
        setContentView(web);
        WebSettings settings = web.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setDomStorageEnabled(true);
        settings.setAllowFileAccess(false);
        settings.setAllowContentAccess(false);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        settings.setSupportZoom(false);
        settings.setCacheMode(WebSettings.LOAD_NO_CACHE);
        WebView.setWebContentsDebuggingEnabled(BuildConfig.DEBUG);
        web.addJavascriptInterface(new Bridge(), "ProductionNative");
        web.setWebViewClient(new WebViewClient() {
            @Override public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) { return true; }
            @Override public WebResourceResponse shouldInterceptRequest(WebView view, WebResourceRequest request) {
                Uri uri = request.getUrl();
                if (!"https".equals(uri.getScheme()) || !"appassets.androidplatform.net".equals(uri.getHost()))
                    return new WebResourceResponse("text/plain", "UTF-8", new ByteArrayInputStream(new byte[0]));
                String path = uri.getPath();
                if (path == null || !path.startsWith("/production-app/") || path.contains("..")) return null;
                try {
                    String type = path.endsWith(".js") ? "application/javascript" : path.endsWith(".css") ? "text/css"
                        : path.endsWith(".png") ? "image/png" : "text/html";
                    return new WebResourceResponse(type, "UTF-8", getAssets().open(path.substring(1)));
                } catch (IOException error) { return new WebResourceResponse("text/plain", "UTF-8", new ByteArrayInputStream("页面资源缺失，请重新安装应用。".getBytes(StandardCharsets.UTF_8))); }
            }
        });
        web.loadUrl(ORIGIN + "/production-app/index.html");
    }

    private void reply(String id, int status, String body) {
        runOnUiThread(() -> { if (!isFinishing()) web.evaluateJavascript("window.productionNativeResult(" + JSONObject.quote(id) + "," + status + "," + JSONObject.quote(body) + ")", null); });
    }

    private final class Bridge {
        @JavascriptInterface public String server() { return server; }
        @JavascriptInterface public void setServer(String value) {
            try {
                URI uri = new URI(value.trim());
                if (!("https".equals(uri.getScheme()) || "http".equals(uri.getScheme())) || uri.getHost() == null
                    || uri.getUserInfo() != null || uri.getQuery() != null || uri.getFragment() != null
                    || !(uri.getPath().isEmpty() || "/".equals(uri.getPath()))) throw new IllegalArgumentException();
                String next = value.trim().replaceAll("/+$", "");
                if (!next.equals(server)) accessToken = "";
                server = next;
                getPreferences(MODE_PRIVATE).edit().putString("server", server).apply();
            } catch (Exception error) { server = ""; accessToken = ""; }
        }
        @JavascriptInterface public void logout() { accessToken = ""; }
        @JavascriptInterface public void request(String id, String path, String method, String body) {
            if (id.length() > 40 || path.length() > 12000 || body.length() > 1000000
                || !path.matches("^/api/(auth/(login|logout|resume|heartbeat)|production-mobile(?:/[^#]*)?|documents/[a-fA-F0-9-]+/obsolete)$")
                || path.contains("..") || !method.matches("GET|POST|PUT")) {
                reply(id, 400, "{\"message\":\"请求无效，请重新打开应用。\"}"); return;
            }
            final String currentServer = server, token = accessToken;
            requests.execute(() -> {
                HttpURLConnection connection = null;
                try {
                    if (currentServer.isEmpty()) { reply(id, 400, "{\"message\":\"请输入有效的服务器地址。\"}"); return; }
                    URL url = new URL(currentServer + path);
                    // HTTP is supported for the existing LAN deployment only. TLS uses platform trust.
                    if ("http".equals(url.getProtocol())) {
                        for (InetAddress address : InetAddress.getAllByName(url.getHost()))
                            if (!address.isSiteLocalAddress() && !address.isLoopbackAddress() && !address.isLinkLocalAddress()) {
                                reply(id, 400, "{\"message\":\"外网服务器请使用 HTTPS 地址；HTTP 仅用于局域网。\"}"); return;
                            }
                    }
                    connection = (HttpURLConnection) url.openConnection();
                    connection.setInstanceFollowRedirects(false);
                    connection.setConnectTimeout(15000); connection.setReadTimeout(30000);
                    connection.setRequestMethod(method);
                    connection.setRequestProperty("Accept", "application/json");
                    if (!token.isEmpty()) connection.setRequestProperty("Authorization", "Bearer " + token);
                    if (!body.isEmpty()) { connection.setDoOutput(true); connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
                        try (OutputStream output = connection.getOutputStream()) { output.write(body.getBytes(StandardCharsets.UTF_8)); } }
                    int status = connection.getResponseCode();
                    InputStream source = status >= 400 ? connection.getErrorStream() : connection.getInputStream();
                    ByteArrayOutputStream buffer = new ByteArrayOutputStream();
                    if (source != null) try (InputStream input = source) { byte[] chunk = new byte[8192]; int count;
                        while ((count = input.read(chunk)) != -1) { if (buffer.size() + count > 8000000) throw new IOException(); buffer.write(chunk, 0, count); } }
                    String response = buffer.toString("UTF-8");
                    if ((path.equals("/api/auth/login") || path.equals("/api/auth/resume")) && status == 200 && server.equals(currentServer)) accessToken = new JSONObject(response).getString("accessToken");
                    if (status == 401 || path.equals("/api/auth/logout")) accessToken = "";
                    reply(id, status, response);
                } catch (Exception error) {
                    android.util.Log.w("ProductionApi", "API request failed: " + method + " " + path.split("\\?")[0], error);
                    reply(id, 503, "{\"message\":\"无法连接服务器，请检查局域网、地址和证书后重试。\"}");
                } finally { if (connection != null) connection.disconnect(); }
            });
        }
        @JavascriptInterface public void scan() { runOnUiThread(() -> {
            if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) requestPermissions(new String[]{Manifest.permission.CAMERA}, 9);
            else startActivityForResult(new Intent(MainActivity.this, ScanActivity.class), 10);
        }); }
    }
    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] grants) {
        super.onRequestPermissionsResult(request, permissions, grants);
        if (request == 9 && grants.length > 0 && grants[0] == PackageManager.PERMISSION_GRANTED) startActivityForResult(new Intent(this, ScanActivity.class), 10);
        else if (request == 9) android.widget.Toast.makeText(this, "未开启相机权限，仍可使用 PDA 扫码键。", android.widget.Toast.LENGTH_LONG).show();
    }
    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request == 10 && result == RESULT_OK && data != null)
            web.evaluateJavascript("window.productionScan(" + JSONObject.quote(data.getStringExtra("code")) + ")", null);
    }
    @Override public void onBackPressed() { web.evaluateJavascript("window.productionBack()", null); }
    @Override protected void onDestroy() { accessToken = ""; requests.shutdownNow(); web.removeJavascriptInterface("ProductionNative"); web.destroy(); super.onDestroy(); }
}
