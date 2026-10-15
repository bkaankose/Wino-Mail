package com.winomail.editor;

import android.os.Handler;
import android.os.Message;
import android.webkit.JavascriptInterface;

/**
 * The page bridge for WebView builds without the AndroidX WebMessageListener feature (for example the WebView of
 * Android 6.0 images). JavaScript calls winoMail.postMessage(json) on a WebView background thread; the message goes
 * to the main-thread handler of AndroidWebViewSession. Written in Java so the app needs no [Export] marshalling,
 * which full trimming cannot analyse.
 */
public final class WinoJavaScriptBridge {
    private final Handler handler;

    public WinoJavaScriptBridge(Handler handler) {
        this.handler = handler;
    }

    @JavascriptInterface
    public void postMessage(String json) {
        Message.obtain(handler, 1, json).sendToTarget();
    }
}
