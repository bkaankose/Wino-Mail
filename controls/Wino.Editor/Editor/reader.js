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
    const documentSanitizeOptions = Object.assign({}, sanitizeOptions, { WHOLE_DOCUMENT: true });

    // The mail lives in a shadow root. Its stylesheets cannot restyle the reader document,
    // the reader's styles cannot leak into the mail, and element ids inside the mail cannot
    // clobber document or window properties. Unlike an iframe, the shadow tree paginates
    // normally when the host prints the message or exports it to PDF.
    const mailRoot = reader.attachShadow({ mode: "open" });
    const fontFaceStyle = document.createElement("style");
    fontFaceStyle.id = "wino-mail-fonts";
    document.head.appendChild(fontFaceStyle);

    const baseStyleText = `
        :host { display: block; }
        .wino-mail-html {
            display: flex;
            flex-direction: column;
            box-sizing: border-box;
            min-height: 100vh;
            color-scheme: light;
        }
        :host([data-mail-scheme="dark"]) .wino-mail-html { color-scheme: dark; }
        .wino-mail-body { flex: 1 0 auto; }
        :where(.wino-mail-body) {
            display: block;
            margin: 0;
            padding: 16px;
            color: CanvasText;
            font-family: var(--wino-font-family, "Segoe UI"), sans-serif;
            font-size: var(--wino-font-size, 15px);
            overflow-wrap: break-word;
        }
        :where(.wino-mail-body) :where(img:not([width]):not([height])) { max-width: 100%; }
        :where(.wino-mail-body) :where(pre) { white-space: pre-wrap; }
        .wino-readability {
            box-sizing: border-box;
            width: min(100%, 760px);
            margin-inline: auto;
            line-height: 1.62;
        }
        .wino-readability p, .wino-readability blockquote, .wino-readability pre { margin: 0 0 1em; }
        .wino-readability h2 { margin: 1.4em 0 .5em; font-size: 1.3em; }
        .wino-readability blockquote { padding-inline-start: 1em; color: GrayText; border-inline-start: 3px solid GrayText; }
        .wino-readability details { margin-top: 1.5em; padding-top: .75em; border-top: 1px solid GrayText; }
        .wino-readability summary { cursor: pointer; font-weight: 600; }
        .wino-readability a { color: LinkText; }
        .wino-readability img { max-width: 100%; height: auto; }
        .wino-readability pre { font-family: Consolas, monospace; }
    `;

    let originalHtml = "";
    let presentationVersion = 0;
    let isDarkTheme = document.documentElement.dataset.theme === "dark";
    let mailHtmlElement = null;
    let mailBodyElement = null;
    let schemeRules = [];
    let declaresDarkSupport = false;
    let requiresLightOnly = false;
    let colorOverrides = [];
    let imageRefreshTimer = 0;

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

    function ensureSanitizer() {
        if (!window.DOMPurify || window.DOMPurify.isSupported !== true ||
            typeof window.DOMPurify.sanitize !== "function") {
            throw new Error("DOMPurify is unavailable; refusing to render untrusted HTML.");
        }
    }

    function sanitize(html, options) {
        ensureSanitizer();
        const sanitized = window.DOMPurify.sanitize(html || "", options || sanitizeOptions);
        if (typeof sanitized !== "string") {
            throw new Error("DOMPurify returned an unexpected result; refusing to render untrusted HTML.");
        }
        return sanitized;
    }

    // Sender color-scheme intent. Meta tags are read from an inert parse of the input and
    // are never inserted: only the hint values are kept.
    function readColorSchemeHints(html) {
        let supportsDark = false;
        let lightOnly = false;
        try {
            const parsed = new DOMParser().parseFromString(html, "text/html");
            parsed.querySelectorAll("meta[name]").forEach(meta => {
                const name = (meta.getAttribute("name") || "").trim().toLowerCase();
                if (name !== "color-scheme" && name !== "supported-color-schemes") return;
                const tokens = (meta.getAttribute("content") || "").toLowerCase().split(/[\s,]+/).filter(Boolean);
                if (tokens.includes("dark")) supportsDark = true;
                else if (tokens.includes("only") && tokens.includes("light")) lightOnly = true;
            });
        } catch (_) {
            // Hints are optional. An unparsable document simply has none.
        }
        return { supportsDark, lightOnly };
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

    // Mail CSS was written for a whole document. Inside the shadow root the mail's <html> and
    // <body> are wrapper elements, so html/body/:root selectors are mapped onto them.
    function rewriteSelector(selector) {
        return selector
            .replace(/(^|[\s>+~,(])(html|body)(?![\w-])/gi, (match, prefix, tag) =>
                prefix + (tag.toLowerCase() === "html" ? ".wino-mail-html" : ".wino-mail-body"))
            .replace(/:root(?![\w-])/gi, ".wino-mail-html");
    }

    function neutralizePosition(style) {
        // Fixed or sticky elements could overlay the reading surface. Mail clients drop them.
        const position = style.getPropertyValue("position").trim().toLowerCase();
        if (position === "fixed" || position === "sticky") {
            style.setProperty("position", "static", style.getPropertyPriority("position"));
        }
    }

    function processRules(rules, context) {
        for (const rule of Array.from(rules)) {
            if (rule instanceof CSSStyleRule) {
                const selector = rewriteSelector(rule.selectorText);
                if (selector !== rule.selectorText) rule.selectorText = selector;
                neutralizePosition(rule.style);
                if (rule.cssRules && rule.cssRules.length) processRules(rule.cssRules, context);
            } else if (rule instanceof CSSFontFaceRule) {
                // @font-face does not apply inside shadow trees, so it is hoisted to the document.
                context.fontFaces.push(rule.cssText);
            } else if (rule instanceof CSSMediaRule) {
                const media = rule.media.mediaText;
                if (/prefers-color-scheme/i.test(media)) {
                    schemeRules.push({ rule, media });
                    if (/prefers-color-scheme\s*:\s*dark/i.test(media)) context.hasDarkRules = true;
                }
                processRules(rule.cssRules, context);
            } else if (rule.cssRules) {
                processRules(rule.cssRules, context);
            }
        }
    }

    function createMailSheets(styleTexts) {
        const context = { fontFaces: [], hasDarkRules: false };
        const sheets = [];
        for (const text of styleTexts) {
            if (!text.trim()) continue;
            try {
                const sheet = new CSSStyleSheet();
                // replaceSync ignores @import, so mail CSS cannot pull remote stylesheets.
                sheet.replaceSync(text);
                processRules(sheet.cssRules, context);
                sheets.push(sheet);
            } catch (_) {
                // A stylesheet the engine cannot parse is dropped, as a mail client would.
            }
        }
        return { sheets, context };
    }

    // Emulates the presentational attributes of <body>, which a <div> would otherwise ignore.
    // These rules have no specificity, so any author rule for body still wins.
    function createBodyHintsStyle(body) {
        const rules = [];
        const colorPattern = /^#?[0-9a-z]+$/i;
        function color(value) {
            const trimmed = String(value || "").trim();
            if (!trimmed || !colorPattern.test(trimmed)) return "";
            return /^[0-9a-f]{3}([0-9a-f]{3})?$/i.test(trimmed) ? `#${trimmed}` : trimmed;
        }
        const bgcolor = color(body.getAttribute("bgcolor"));
        const text = color(body.getAttribute("text"));
        const link = color(body.getAttribute("link"));
        const background = String(body.getAttribute("background") || "").trim();
        const declarations = [];
        if (bgcolor) declarations.push(`background-color: ${bgcolor};`);
        if (text) declarations.push(`color: ${text};`);
        if (/^(https?:|data:image\/)/i.test(background)) {
            declarations.push(`background-image: url("${background.replace(/["\\\n\r]/g, "")}");`);
        }
        if (declarations.length) rules.push(`:where(.wino-mail-body) { ${declarations.join(" ")} }`);
        if (link) rules.push(`:where(.wino-mail-body) :where(a:link) { color: ${link}; }`);
        const style = document.createElement("style");
        style.textContent = rules.join("\n");
        return style;
    }

    function copyAttributes(source, target, names) {
        for (const name of names) {
            const value = source.getAttribute(name);
            if (value !== null) target.setAttribute(name, value);
        }
    }

    function buildMailTree(sanitizedDocumentHtml, useReadability, readabilityHtml) {
        const parsed = new DOMParser().parseFromString(sanitizedDocumentHtml, "text/html");
        const styleTexts = [];
        if (!useReadability) {
            parsed.querySelectorAll("style").forEach(style => styleTexts.push(style.textContent || ""));
        }
        parsed.querySelectorAll("style").forEach(style => style.remove());
        parsed.querySelectorAll("[style]").forEach(element => neutralizePosition(element.style));

        const htmlElement = document.createElement("div");
        htmlElement.className = "wino-mail-html";
        const bodyElement = document.createElement("div");

        if (useReadability) {
            bodyElement.className = "wino-mail-body wino-readability";
            const article = new DOMParser().parseFromString(readabilityHtml, "text/html");
            for (const child of Array.from(article.body.childNodes)) {
                bodyElement.appendChild(document.importNode(child, true));
            }
        } else {
            copyAttributes(parsed.documentElement, htmlElement, ["lang", "dir", "style"]);
            if (parsed.documentElement.getAttribute("class")) {
                htmlElement.className += ` ${parsed.documentElement.getAttribute("class")}`;
            }
            copyAttributes(parsed.body, bodyElement, ["lang", "dir", "style", "id"]);
            bodyElement.className = `wino-mail-body ${parsed.body.getAttribute("class") || ""}`.trim();
            for (const child of Array.from(parsed.body.childNodes)) {
                bodyElement.appendChild(document.importNode(child, true));
            }
        }

        htmlElement.appendChild(bodyElement);
        return {
            htmlElement,
            bodyElement,
            styleTexts,
            bodyHints: useReadability ? document.createElement("style") : createBodyHintsStyle(parsed.body)
        };
    }

    function propagateBodyBackground() {
        // In a real document the body background paints the whole canvas when <html> has none.
        if (!mailHtmlElement || !mailBodyElement) return;
        mailHtmlElement.style.removeProperty("background-color");
        mailHtmlElement.style.removeProperty("background-image");
        const htmlStyle = getComputedStyle(mailHtmlElement);
        const htmlBackground = window.WinoMailColors.parseColor(htmlStyle.backgroundColor);
        if ((htmlBackground && htmlBackground.a > 0) || htmlStyle.backgroundImage !== "none") return;
        const bodyStyle = getComputedStyle(mailBodyElement);
        const bodyBackground = window.WinoMailColors.parseColor(bodyStyle.backgroundColor);
        if (bodyBackground && bodyBackground.a > 0) {
            mailHtmlElement.style.setProperty("background-color", bodyStyle.backgroundColor);
        }
    }

    function forceColorScheme(scheme) {
        for (const entry of schemeRules) {
            entry.rule.media.mediaText = entry.media.replace(
                /\(\s*prefers-color-scheme\s*:\s*(dark|light)\s*\)/gi,
                (match, value) => value.toLowerCase() === scheme ? "(min-width: 0px)" : "(max-width: 0px)");
        }
    }

    function clearColorOverrides() {
        for (const entry of colorOverrides) {
            if (entry.style === null) entry.element.removeAttribute("style");
            else entry.element.setAttribute("style", entry.style);
        }
        colorOverrides = [];
    }

    function applyColorOverrides(overrides) {
        const saved = new Set();
        for (const override of overrides) {
            if (!saved.has(override.element)) {
                saved.add(override.element);
                colorOverrides.push({ element: override.element, style: override.element.getAttribute("style") });
            }
            override.element.style.setProperty(override.property, override.value, "important");
        }
    }

    // Decides how the current message is shown:
    // - light theme, or a sender that asked for "light only": the original colors;
    // - dark theme with sender dark support: the sender's own dark styles;
    // - dark theme otherwise: the original design with selective color adaptation.
    function applyColorPolicy() {
        clearColorOverrides();
        const scheme = isDarkTheme && declaresDarkSupport && !requiresLightOnly ? "dark" : "light";
        const adapt = isDarkTheme && !declaresDarkSupport && !requiresLightOnly;
        const surface = isDarkTheme && !requiresLightOnly ? "dark" : "light";

        document.documentElement.dataset.surface = surface;
        reader.dataset.mailScheme = scheme;
        forceColorScheme(scheme);
        if (!mailHtmlElement) return;

        propagateBodyBackground();
        if (adapt && window.WinoMailColors) {
            const surfaceColor = getComputedStyle(document.documentElement).backgroundColor;
            applyColorOverrides(window.WinoMailColors.computeDarkOverrides(mailHtmlElement, surfaceColor));
        }
    }

    // Image backdrops depend on the rendered image size, which is unknown until it loads.
    function watchImageLoads() {
        if (!mailHtmlElement) return;
        const refresh = () => {
            if (!isDarkTheme || declaresDarkSupport || requiresLightOnly) return;
            window.clearTimeout(imageRefreshTimer);
            imageRefreshTimer = window.setTimeout(() => {
                if (mailHtmlElement) applyColorPolicy();
            }, 150);
        };
        mailHtmlElement.querySelectorAll("img").forEach(image => {
            if (!image.complete) image.addEventListener("load", refresh, { once: true });
        });
    }

    function scrollToFragment(fragment) {
        let name = fragment;
        try { name = decodeURIComponent(fragment); } catch (_) { }
        if (!name) return;
        const target = mailRoot.getElementById(name) ||
            Array.from(mailRoot.querySelectorAll("a[name]")).find(anchor => anchor.getAttribute("name") === name);
        if (target) target.scrollIntoView({ block: "start" });
    }

    function render(base64Html, linkify, mode) {
        const decodedHtml = decode(base64Html);
        const useReadability = Number(mode) === readabilityMode;
        const hints = readColorSchemeHints(decodedHtml);
        let presentation;
        let documentHtml;
        if (useReadability) {
            const sanitizedInput = sanitize(decodedHtml);
            presentation = getReadabilityPresentation(sanitizedInput);
            presentation.html = sanitize(presentation.html);
            documentHtml = "";
        } else {
            documentHtml = sanitize(decodedHtml, documentSanitizeOptions);
            presentation = { html: "", direction: "", language: "" };
        }

        const tree = buildMailTree(documentHtml, useReadability, presentation.html);
        schemeRules = [];
        const { sheets, context } = createMailSheets(tree.styleTexts);

        const version = beginPresentationUpdate();
        clearColorOverrides();
        originalHtml = decodedHtml;
        declaresDarkSupport = !useReadability && (hints.supportsDark || context.hasDarkRules);
        requiresLightOnly = !useReadability && hints.lightOnly && !declaresDarkSupport;
        applyDocumentLanguage(presentation.direction, presentation.language);

        const baseStyle = document.createElement("style");
        baseStyle.textContent = baseStyleText;
        fontFaceStyle.textContent = context.fontFaces.join("\n");
        mailRoot.replaceChildren(baseStyle, tree.bodyHints, tree.htmlElement);
        mailRoot.adoptedStyleSheets = sheets;
        mailHtmlElement = tree.htmlElement;
        mailBodyElement = tree.bodyElement;

        if (linkify && window.linkifyElement) {
            window.linkifyElement(mailBodyElement, { target: "_blank", rel: "noopener noreferrer", ignoreTags: ["A", "SCRIPT", "STYLE", "TEXTAREA", "CODE", "PRE"] });
        }

        applyColorPolicy();
        watchImageLoads();
        revealAfterStylesSettle(version);
        return true;
    }

    function setTheme(isDark) {
        const version = beginPresentationUpdate();
        isDarkTheme = Boolean(isDark);
        document.documentElement.dataset.theme = isDarkTheme ? "dark" : "light";
        applyColorPolicy();
        revealAfterStylesSettle(version);
    }

    function setTypography(fontFamily, fontSize) {
        reader.style.setProperty("--wino-font-family", fontFamily || "Segoe UI");
        reader.style.setProperty("--wino-font-size", `${Math.max(8, Math.min(72, Number(fontSize) || 15))}px`);
        if (mailHtmlElement && isDarkTheme) applyColorPolicy();
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
        // Clicks inside the shadow tree are retargeted to the host, so the path is inspected.
        const anchor = event.composedPath().find(node =>
            node instanceof Element && node.localName === "a" && node.hasAttribute("href"));
        if (!anchor) return;
        event.preventDefault();
        const href = (anchor.getAttribute("href") || "").trim();
        if (href.startsWith("#")) {
            scrollToFragment(href.slice(1));
            return;
        }
        post({ type: "navigation", uri: anchor.href });
    });

    window.WinoRenderer = {
        render,
        clear() {
            beginPresentationUpdate();
            window.clearTimeout(imageRefreshTimer);
            clearColorOverrides();
            originalHtml = "";
            schemeRules = [];
            declaresDarkSupport = false;
            requiresLightOnly = false;
            mailHtmlElement = null;
            mailBodyElement = null;
            fontFaceStyle.textContent = "";
            mailRoot.adoptedStyleSheets = [];
            mailRoot.replaceChildren();
            applyDocumentLanguage("", "");
            applyColorPolicy();
        },
        getOriginalHtml() { return originalHtml; },
        getColorPolicy() {
            if (!isDarkTheme || requiresLightOnly) return "original";
            return declaresDarkSupport ? "sender-dark" : "adapted";
        },
        setTheme,
        setTypography,
        setAccessibility
    };

    applyColorPolicy();

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
