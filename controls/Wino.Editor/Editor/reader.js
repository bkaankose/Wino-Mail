(function () {
    "use strict";
    const reader = document.getElementById("wino-reader");
    const readabilityMode = 1;
    const sanitizeOptions = {
        USE_PROFILES: { html: true },
        FORBID_TAGS: [
            "script", "form", "input", "button", "textarea", "select", "option",
            "fieldset", "legend", "output", "datalist", "iframe", "frame", "frameset",
            "object", "embed", "applet", "base", "meta", "link", "template"
        ],
        FORBID_CONTENTS: [
            "script", "form", "iframe", "frame", "frameset", "object", "embed",
            "applet", "template"
        ]
    };
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

    function sanitize(html) {
        if (!window.DOMPurify || window.DOMPurify.isSupported !== true ||
            typeof window.DOMPurify.sanitize !== "function") {
            throw new Error("DOMPurify is unavailable; refusing to render untrusted HTML.");
        }

        const sanitized = window.DOMPurify.sanitize(html || "", sanitizeOptions);
        if (typeof sanitized !== "string") {
            throw new Error("DOMPurify returned an unexpected result; refusing to render untrusted HTML.");
        }
        return sanitized;
    }

    function getReadabilityPresentation(sanitizedHtml) {
        let article = null;
        try {
            const detachedDocument = new DOMParser().parseFromString(sanitizedHtml, "text/html");
            article = new window.Readability(detachedDocument).parse();
        } catch (_) {
            article = null;
        }

        return {
            html: article && typeof article.content === "string" && article.content.trim()
                ? article.content
                : sanitizedHtml,
            direction: article && typeof article.dir === "string" ? article.dir : "",
            language: article && typeof article.lang === "string" ? article.lang : ""
        };
    }

    function applyDocumentLanguage(direction, language) {
        if (["ltr", "rtl", "auto"].includes(direction)) reader.setAttribute("dir", direction);
        else reader.removeAttribute("dir");

        if (language && language.length <= 35) reader.setAttribute("lang", language);
        else reader.removeAttribute("lang");
    }

    function render(base64Html, linkify, mode) {
        const decodedHtml = decode(base64Html);
        const sanitizedInput = sanitize(decodedHtml);
        const useReadability = Number(mode) === readabilityMode;
        const presentation = useReadability
            ? getReadabilityPresentation(sanitizedInput)
            : { html: sanitizedInput, direction: "", language: "" };
        const finalHtml = sanitize(presentation.html);
        const version = beginPresentationUpdate();
        originalHtml = decodedHtml;
        reader.classList.toggle("wino-reader", useReadability);
        applyDocumentLanguage(presentation.direction, presentation.language);
        reader.innerHTML = finalHtml;
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
        const surfaceColor = isDark ? "Canvas" : "#ffffff";
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
            reader.classList.remove("wino-reader");
            applyDocumentLanguage("", "");
            while (reader.firstChild) reader.removeChild(reader.firstChild);
        },
        getOriginalHtml() { return originalHtml; },
        setTheme,
        setTypography,
        setAccessibility
    };
    function announceReady() {
        document.removeEventListener("DOMContentLoaded", announceReady);
        const status = winoGetRendererStatus();
        post(status === "ready"
            ? { type: "ready" }
            : { type: "initializationError", error: status });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", announceReady);
    } else {
        announceReady();
    }
}());
