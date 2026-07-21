(function () {
    "use strict";
    const reader = document.getElementById("wino-reader");
    let originalHtml = "";

    function post(message) {
        if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(message);
        else if (window.external && typeof window.external.notify === "function") window.external.notify(JSON.stringify(message));
    }

    function decode(value) {
        const binary = atob(value || "");
        let encoded = "";
        for (let index = 0; index < binary.length; index += 1) {
            encoded += `%${binary.charCodeAt(index).toString(16).padStart(2, "0")}`;
        }
        return decodeURIComponent(encoded);
    }

    function render(base64Html, linkify) {
        originalHtml = decode(base64Html);
        reader.innerHTML = originalHtml;
        if (linkify && window.linkifyElement) {
            window.linkifyElement(reader, { target: "_blank", rel: "noopener noreferrer", ignoreTags: ["A", "SCRIPT", "STYLE", "TEXTAREA", "CODE", "PRE"] });
        }
        return true;
    }

    function setTheme(isDark) {
        document.documentElement.dataset.theme = isDark ? "dark" : "light";
        if (window.DarkReader) {
            if (isDark) window.DarkReader.enable({ brightness: 100, contrast: 90, sepia: 0 });
            else window.DarkReader.disable();
        }
    }

    function setTypography(fontFamily, fontSize) {
        document.documentElement.style.setProperty("--wino-font-family", fontFamily || "Segoe UI");
        document.documentElement.style.setProperty("--wino-font-size", `${Math.max(8, Math.min(72, Number(fontSize) || 15))}px`);
    }

    function setAccessibility(subject, sender, date, bodyContext) {
        const context = [subject, sender, date].filter(Boolean).join(", ");
        reader.setAttribute("aria-label", context || "Message body");
        if (bodyContext) reader.setAttribute("aria-description", bodyContext);
        else reader.removeAttribute("aria-description");
    }

    document.addEventListener("click", event => {
        const anchor = event.target.closest("a[href]");
        if (!anchor) return;
        event.preventDefault();
        post({ type: "navigation", uri: anchor.href });
    });

    window.WinoRenderer = {
        render,
        clear() {
            originalHtml = "";
            while (reader.firstChild) reader.removeChild(reader.firstChild);
        },
        getOriginalHtml() { return originalHtml; },
        setTheme,
        setTypography,
        setAccessibility
    };
    function announceReady() {
        document.removeEventListener("DOMContentLoaded", announceReady);
        post({ type: "ready" });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", announceReady);
    } else {
        announceReady();
    }
}());
