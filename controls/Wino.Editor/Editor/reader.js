(function () {
    "use strict";
    const reader = document.getElementById("wino-reader");
    let originalHtml = "";
    let presentationVersion = 0;

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

    function beginPresentationUpdate() {
        presentationVersion += 1;
        reader.style.visibility = "hidden";
        return presentationVersion;
    }

    function revealAfterStylesSettle(version) {
        requestAnimationFrame(() => {
            requestAnimationFrame(() => {
                if (version === presentationVersion) reader.style.visibility = "visible";
            });
        });
    }

    function render(base64Html, linkify) {
        const version = beginPresentationUpdate();
        originalHtml = decode(base64Html);
        reader.innerHTML = originalHtml;
        if (linkify && window.linkifyElement) {
            window.linkifyElement(reader, { target: "_blank", rel: "noopener noreferrer", ignoreTags: ["A", "SCRIPT", "STYLE", "TEXTAREA", "CODE", "PRE"] });
        }
        revealAfterStylesSettle(version);
        return true;
    }

    function setTheme(isDark) {
        const version = beginPresentationUpdate();
        document.documentElement.dataset.theme = isDark ? "dark" : "light";
        if (window.DarkReader) {
            if (isDark) window.DarkReader.enable({ brightness: 100, contrast: 90, sepia: 0 });
            else window.DarkReader.disable();
        }
        const surfaceColor = isDark ? "transparent" : "#ffffff";
        document.documentElement.style.setProperty("background-color", surfaceColor, "important");
        document.body.style.setProperty("background-color", surfaceColor, "important");
        revealAfterStylesSettle(version);
    }

    function setTypography(fontFamily, fontSize) {
        reader.style.fontFamily = fontFamily || "Segoe UI";
        reader.style.fontSize = `${Math.max(8, Math.min(72, Number(fontSize) || 15))}px`;
    }

    function setAccessibility(subject, sender, date, bodyAutomationName, plainTextFallbackAutomationName, accessibleText) {
        const context = [subject, sender, date].filter(Boolean).join(", ");
        const bodyName = bodyAutomationName || "Message body";
        reader.setAttribute("aria-label", context ? `${bodyName}, ${context}` : bodyName);
        reader.dataset.plainTextFallbackAutomationName = plainTextFallbackAutomationName || "Plain text message";
        if (accessibleText) reader.setAttribute("aria-description", accessibleText);
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
            beginPresentationUpdate();
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
