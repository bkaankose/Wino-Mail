(function () {
    "use strict";
    const reader = document.getElementById("wino-reader");
    const readerHeading = document.getElementById("wino-reader-heading");
    const readabilityMode = 1;
    // Share of a message still painted light under the sender's own dark rules at which those
    // rules are treated as ineffective.
    const ineffectiveDarkSupportCoverage = 0.5;
    const darkMethods = ["adaptive", "inverted"];
    const darkColorAttribute = "data-wino-dk";
    const darkColorSpecificity = "#wino-dk#wino-dk:not(#wino-dk)";
    const forcedColors = window.matchMedia("(forced-colors: active)");
    const sanitizeOptions = {
        USE_PROFILES: { html: true },
        FORBID_TAGS: [
            "script", "form", "input", "button", "textarea", "select", "option",
            "fieldset", "legend", "output", "datalist", "iframe", "frame", "frameset",
            "object", "embed", "applet", "base", "meta", "link", "template"
        ],
        // Extends DOMPurify's defaults, which drop <title>, <noscript> and similar text from removed elements.
        ADD_FORBID_CONTENTS: [
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
    const darkColorStyle = document.createElement("style");
    const schemeBackgroundStyle = document.createElement("style");
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
        @media screen {
            :host([data-mail-scheme="dark"]) .wino-mail-html { color-scheme: dark; }
            :host([data-mail-filter="inverted"]) .wino-mail-html { filter: invert(1) hue-rotate(180deg); }
            :host([data-mail-filter="inverted"]) :is(img, video, canvas):not(svg *),
            :host([data-mail-filter="inverted"]) svg:not(svg *) { filter: invert(1) hue-rotate(180deg) brightness(.85); }
        }
        @media (forced-colors: active) {
            :host([data-mail-filter]) .wino-mail-html,
            :host([data-mail-filter]) :is(img, video, canvas, svg) { filter: none; }
        }
        /* A host inset (setContentInset) keeps text off the reader edge even when the mail zeroes its own padding. */
        :host([data-inset]) .wino-mail-html { padding: var(--wino-reader-inset, 0px) !important; }
        .wino-mail-body { flex: 1 0 auto; }
        :where(.wino-mail-body) {
            display: block;
            margin: 0;
            padding: var(--wino-body-padding, 16px);
            color: CanvasText;
            font-family: var(--wino-font-family, "Segoe UI"), sans-serif;
            font-size: var(--wino-font-size, 15px);
            overflow-wrap: break-word;
        }
        :where(.wino-mail-body) :where(img:not([width]):not([height])) { max-width: 100%; }
        :where(.wino-mail-body) :where(pre) { white-space: pre-wrap; }
        .wino-mail-body a[href]:hover { text-decoration-line: underline !important; text-decoration-thickness: 2px !important; }
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
        .wino-text-fallback {
            position: absolute; width: 1px; height: 1px; margin: -1px; padding: 0;
            overflow: hidden; clip-path: inset(50%); white-space: pre-wrap; border: 0;
        }
    `;

    let originalHtml = "";
    let presentationVersion = 0;
    let isDarkTheme = document.documentElement.dataset.theme === "dark";
    let mailHtmlElement = null;
    let mailBodyElement = null;
    let schemeRules = [];
    let declaresDarkSupport = false;
    let darkMethod = darkMethods.includes(document.documentElement.dataset.darkMethod)
        ? document.documentElement.dataset.darkMethod
        : "adaptive";
    let adaptsColors = false;
    let invertsColors = false;
    let darkColorElements = [];
    let inlineColorRestores = [];
    let imageRefreshTimer = 0;
    let fallbackElement = null;
    let fallbackName = "Plain text message";
    let fallbackText = "";
    const maxImageFetchesPerMessage = 32;
    let remoteContentAllowed = false;
    let imageFetchRequestId = 0;
    let imageFetchesByUri = new Map();
    let imageFetchesById = new Map();

    function post(message) {
        message.sessionId = window.winoSessionId;
        if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(message);
        else if (window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.winoMail) window.webkit.messageHandlers.winoMail.postMessage(JSON.stringify(message));
        // Android WebView: the WebMessageListener (or JavaScript interface) object the Editor.Android session injects.
        else if (window.winoMail && typeof window.winoMail.postMessage === "function") window.winoMail.postMessage(JSON.stringify(message));
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

    let isStyleMarkerHookInstalled = false;

    function sanitize(html, options) {
        ensureSanitizer();
        if (!isStyleMarkerHookInstalled) {
            // SAFE_FOR_XML removes any element whose text contains "<!", so a <style> wrapped in
            // /*<![CDATA[*/ or <!-- --> markers lost every rule. CSS ignores the markers.
            window.DOMPurify.addHook("uponSanitizeElement", (node, data) => {
                if (data.tagName === "style" && node.textContent) {
                    node.textContent = node.textContent.replace(/<!\[CDATA\[|\]\]>|<!--|-->/g, "");
                }
            });
            isStyleMarkerHookInstalled = true;
        }
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
        try {
            const parsed = new DOMParser().parseFromString(html, "text/html");
            parsed.querySelectorAll("meta[name]").forEach(meta => {
                const name = (meta.getAttribute("name") || "").trim().toLowerCase();
                if (name !== "color-scheme" && name !== "supported-color-schemes") return;
                const tokens = (meta.getAttribute("content") || "").toLowerCase().split(/[\s,]+/).filter(Boolean);
                if (tokens.includes("dark")) supportsDark = true;
            });
        } catch (_) {
            // Hints are optional. An unparsable document simply has none.
        }
        return { supportsDark };
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
                    schemeRules.push({ rule, queries: Array.from(rule.media) });
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

    function getCanvasBackground() {
        const htmlStyle = getComputedStyle(mailHtmlElement);
        const htmlBackground = window.WinoMailColors.parseColor(htmlStyle.backgroundColor);
        if ((htmlBackground && htmlBackground.a > 0) || htmlStyle.backgroundImage !== "none") return "";
        const bodyStyle = getComputedStyle(mailBodyElement);
        const bodyBackground = window.WinoMailColors.parseColor(bodyStyle.backgroundColor);
        return bodyBackground && bodyBackground.a > 0 ? bodyStyle.backgroundColor : "";
    }

    // In a real document the body background paints the whole canvas when <html> has none.
    function propagateBodyBackground(scheme) {
        schemeBackgroundStyle.textContent = "";
        if (!mailHtmlElement || !mailBodyElement) return;
        mailHtmlElement.style.removeProperty("background-color");
        mailHtmlElement.style.removeProperty("background-image");
        forceColorScheme("light");
        const lightBackground = getCanvasBackground();
        if (scheme === "dark") {
            forceColorScheme("dark");
            const darkBackground = getCanvasBackground();
            if (darkBackground) {
                schemeBackgroundStyle.textContent =
                    `@media screen { .wino-mail-html { background-color: ${darkBackground} !important; } }`;
            }
        }
        if (lightBackground) mailHtmlElement.style.setProperty("background-color", lightBackground);
    }

    function resolveColorScheme(query, scheme) {
        return query.replace(/\(\s*prefers-color-scheme\s*:\s*(dark|light)\s*\)/gi,
            (match, value) => value.toLowerCase() === scheme ? "(min-width: 0px)" : "(max-width: 0px)");
    }

    function restrictQuery(query, type) {
        const typed = /^\s*(only\s+|not\s+)?([a-z][\w-]*)(?:\s+and\s+([\s\S]+?))?\s*$/i.exec(query);
        if (!typed) {
            return /^\s*\(/.test(query) && !/\bor\b/i.test(query)
                ? `${type} and ${query.trim()}`
                : `${type} and (${query.trim()})`;
        }
        const mediaType = typed[2].toLowerCase();
        const matchesType = mediaType === "all" || mediaType === type;
        const condition = typed[3];
        if (!/^not/i.test(typed[1] || "")) {
            if (!matchesType) return "not all";
            return condition ? `${type} and ${condition}` : type;
        }
        if (!matchesType) return type;
        return condition ? `${type} and (not (${condition}))` : "not all";
    }

    function forceColorScheme(scheme) {
        for (const entry of schemeRules) {
            const queries = scheme === "dark"
                ? entry.queries.flatMap(query => [
                    restrictQuery(resolveColorScheme(query, "dark"), "screen"),
                    restrictQuery(resolveColorScheme(query, "light"), "print")])
                : entry.queries.map(query => resolveColorScheme(query, "light"));
            entry.rule.media.mediaText = queries.join(", ");
        }
    }

    function clearColorOverrides() {
        for (const entry of inlineColorRestores) {
            if (entry.style === null) entry.element.removeAttribute("style");
            else entry.element.setAttribute("style", entry.style);
        }
        for (const element of darkColorElements) element.removeAttribute(darkColorAttribute);
        inlineColorRestores = [];
        darkColorElements = [];
        darkColorStyle.textContent = "";
    }

    function applyColorOverrides(overrides) {
        const byElement = new Map();
        for (const override of overrides) {
            let list = byElement.get(override.element);
            if (!list) byElement.set(override.element, list = []);
            list.push(override);
        }

        const screenRules = [];
        const printRules = [];
        byElement.forEach((list, element) => {
            const id = String(darkColorElements.length + 1);
            const selector = `:is([${darkColorAttribute}="${id}"], ${darkColorSpecificity})`;
            const screen = [];
            const print = [];
            const originalStyle = element.getAttribute("style");
            for (const override of list) {
                screen.push(`${override.property}: ${override.value} !important;`);
                if (element.style.getPropertyPriority(override.property) !== "important") continue;
                print.push(`${override.property}: ${element.style.getPropertyValue(override.property)} !important;`);
                element.style.removeProperty(override.property);
            }
            if (print.length) {
                inlineColorRestores.push({ element, style: originalStyle });
                printRules.push(`${selector} { ${print.join(" ")} }`);
            }
            element.setAttribute(darkColorAttribute, id);
            darkColorElements.push(element);
            screenRules.push(`${selector} { ${screen.join(" ")} }`);
        });

        darkColorStyle.textContent =
            `@media screen {\n${screenRules.join("\n")}\n}\n@media print {\n${printRules.join("\n")}\n}`;
    }

    function applyMailScheme(scheme) {
        reader.dataset.mailScheme = scheme;
        forceColorScheme(scheme);
        propagateBodyBackground(scheme);
    }

    // Decides how the current message is shown:
    // - light theme: the original colors;
    // - dark theme with sender dark support: the sender's own dark styles;
    // - dark theme otherwise: the original design with selective color adaptation.
    // Some senders declare dark support, but their dark rules only target other clients
    // (such as Outlook's [data-ogsc] selectors) or swap a logo, so the message stays mostly
    // white. Such a message keeps the sender's dark rules and is adapted on top of them.
    function applyColorPolicy() {
        clearColorOverrides();
        delete reader.dataset.mailFilter;
        adaptsColors = false;
        invertsColors = false;
        const darkens = isDarkTheme && !forcedColors.matches;
        document.documentElement.dataset.surface = isDarkTheme ? "dark" : "light";
        applyMailScheme(darkens && declaresDarkSupport ? "dark" : "light");
        if (!mailHtmlElement || !darkens || !window.WinoMailColors) return;

        if (declaresDarkSupport &&
            window.WinoMailColors.lightSurfaceCoverage(mailHtmlElement) < ineffectiveDarkSupportCoverage) return;

        if (darkMethod === "inverted") {
            if (declaresDarkSupport) applyMailScheme("light");
            invertsColors = true;
            reader.dataset.mailFilter = "inverted";
            return;
        }

        adaptsColors = true;
        const surfaceColor = getComputedStyle(document.documentElement).backgroundColor;
        applyColorOverrides(window.WinoMailColors.computeDarkOverrides(mailHtmlElement, surfaceColor));
    }

    function scheduleColorRefresh() {
        window.clearTimeout(imageRefreshTimer);
        imageRefreshTimer = window.setTimeout(() => {
            if (mailHtmlElement) applyColorPolicy();
        }, 150);
    }

    // Image backdrops depend on the rendered image size, which is unknown until it loads.
    function watchImageLoads() {
        if (!mailHtmlElement) return;
        const refresh = () => {
            if (adaptsColors) scheduleColorRefresh();
        };
        mailHtmlElement.querySelectorAll("img").forEach(image => {
            const fetchable = remoteContentAllowed && getRemoteImageUri(image) !== "";
            if (image.complete) {
                if (fetchable && image.naturalWidth === 0) requestImageFetch(image);
                return;
            }
            image.addEventListener("load", refresh, { once: true });
            image.addEventListener("error", refresh, { once: true });
            if (fetchable) image.addEventListener("error", () => requestImageFetch(image), { once: true });
        });
    }

    function getRemoteImageUri(image) {
        const uri = image.currentSrc || image.src || "";
        return /^https?:\/\//i.test(uri) ? uri : "";
    }

    function requestImageFetch(image) {
        if (!remoteContentAllowed || !image.isConnected) return;
        const uri = getRemoteImageUri(image);
        if (!uri) return;
        const existing = imageFetchesByUri.get(uri);
        if (existing) {
            if (existing.dataUri) applySuppliedImage(image, existing.dataUri);
            else existing.images.push(image);
            return;
        }
        if (imageFetchesByUri.size >= maxImageFetchesPerMessage) return;
        imageFetchRequestId += 1;
        const entry = { requestId: imageFetchRequestId, images: [image] };
        imageFetchesByUri.set(uri, entry);
        imageFetchesById.set(entry.requestId, entry);
        post({ type: "imageFetch", requestId: entry.requestId, uri });
    }

    function supplyImage(requestId, dataUri) {
        const entry = imageFetchesById.get(Number(requestId));
        if (!entry || typeof dataUri !== "string" || !/^data:image\//i.test(dataUri)) return false;
        imageFetchesById.delete(entry.requestId);
        entry.dataUri = dataUri;
        entry.images.forEach(image => applySuppliedImage(image, dataUri));
        entry.images = [];
        if (adaptsColors) scheduleColorRefresh();
        return true;
    }

    function applySuppliedImage(image, dataUri) {
        if (!image.isConnected) return;
        image.removeAttribute("srcset");
        image.removeAttribute("sizes");
        const parent = image.parentElement;
        if (parent && parent.localName === "picture") {
            parent.querySelectorAll(":scope > source").forEach(source => source.remove());
        }
        image.addEventListener("load", () => {
            if (adaptsColors) scheduleColorRefresh();
        }, { once: true });
        image.src = dataUri;
    }

    function resetImageFetches() {
        imageFetchesByUri = new Map();
        imageFetchesById = new Map();
    }

    document.addEventListener("wino-backdrop-measured", () => {
        if (isDarkTheme) scheduleColorRefresh();
    });

    forcedColors.addEventListener("change", () => {
        if (mailHtmlElement) applyColorPolicy();
    });

    function scrollToFragment(fragment) {
        let name = fragment;
        try { name = decodeURIComponent(fragment); } catch (_) { }
        if (!name) return;
        const target = mailRoot.getElementById(name) ||
            Array.from(mailRoot.querySelectorAll("a[name]")).find(anchor => anchor.getAttribute("name") === name);
        if (target) target.scrollIntoView({ block: "start" });
    }

    function render(base64Html, linkify, mode, allowRemoteContent) {
        reportLink(null);
        resetImageFetches();
        remoteContentAllowed = allowRemoteContent === true;
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
        if (window.WinoMailColors) window.WinoMailColors.resetBackdropCache();
        originalHtml = decodedHtml;
        declaresDarkSupport = !useReadability && (hints.supportsDark || context.hasDarkRules);
        applyDocumentLanguage(presentation.direction, presentation.language);

        const baseStyle = document.createElement("style");
        baseStyle.textContent = baseStyleText;
        fontFaceStyle.textContent = context.fontFaces.join("\n");
        mailRoot.replaceChildren(baseStyle, tree.bodyHints, darkColorStyle, schemeBackgroundStyle, tree.htmlElement);
        mailRoot.adoptedStyleSheets = sheets;
        mailHtmlElement = tree.htmlElement;
        mailBodyElement = tree.bodyElement;

        if (linkify && window.linkifyElement) {
            window.linkifyElement(mailBodyElement, { target: "_blank", rel: "noopener noreferrer", ignoreTags: ["A", "SCRIPT", "STYLE", "TEXTAREA", "CODE", "PRE"] });
        }

        applyColorPolicy();
        watchImageLoads();
        updateTextFallback();
        revealAfterStylesSettle(version);
        return true;
    }

    function setTheme(isDark, method) {
        const version = beginPresentationUpdate();
        isDarkTheme = Boolean(isDark);
        if (darkMethods.includes(method)) darkMethod = method;
        document.documentElement.dataset.theme = isDarkTheme ? "dark" : "light";
        document.documentElement.dataset.darkMethod = darkMethod;
        applyColorPolicy();
        revealAfterStylesSettle(version);
    }

    function setTypography(fontFamily, fontSize) {
        reader.style.setProperty("--wino-font-family", fontFamily || "Segoe UI");
        reader.style.setProperty("--wino-font-size", `${Math.max(8, Math.min(72, Number(fontSize) || 15))}px`);
        if (mailHtmlElement && isDarkTheme) applyColorPolicy();
    }

    // Optional inset around the whole mail (iOS). The default body padding shrinks so ordinary mails keep 16px in
    // total, and mails that zero their body padding still keep the inset. Zero restores the default document.
    function setContentInset(inset) {
        const value = Math.max(0, Math.min(48, Number(inset) || 0));
        if (value > 0) {
            reader.style.setProperty("--wino-reader-inset", `${value}px`);
            reader.style.setProperty("--wino-body-padding", `${Math.max(0, 16 - value)}px`);
            reader.setAttribute("data-inset", "");
        } else {
            reader.style.removeProperty("--wino-reader-inset");
            reader.style.removeProperty("--wino-body-padding");
            reader.removeAttribute("data-inset");
        }
    }

    // Keyboard focus enters the message on a visually hidden heading, so screen readers announce only the
    // message context and start reading at the top of the mail. A focusable container around the mail made
    // NVDA read the whole message as one block, and the plain-text version used as its description was read
    // in full again every time the reading cursor entered the body.
    function setAccessibility(subject, sender, date, bodyAutomationName, plainTextFallbackAutomationName, accessibleText) {
        const context = [subject, sender, date].filter(Boolean).join(", ");
        const bodyName = bodyAutomationName || "Message body";
        readerHeading.textContent = context ? `${bodyName}, ${context}` : bodyName;
        document.title = subject || bodyName;
        fallbackName = plainTextFallbackAutomationName || "Plain text message";
        fallbackText = accessibleText || "";
        updateTextFallback();
    }

    // Screen readers read the rendered mail itself. The plain-text version is offered only when the
    // rendered mail has no readable text, such as a message made of images without alternative text.
    function updateTextFallback() {
        if (fallbackElement) fallbackElement.remove();
        fallbackElement = null;
        if (!fallbackText || !mailBodyElement || hasReadableContent(mailBodyElement)) return;
        fallbackElement = document.createElement("section");
        fallbackElement.className = "wino-text-fallback";
        fallbackElement.setAttribute("aria-label", fallbackName);
        fallbackElement.textContent = fallbackText;
        mailRoot.appendChild(fallbackElement);
    }

    function hasReadableContent(element) {
        if ((element.textContent || "").trim()) return true;
        return Array.from(element.querySelectorAll("img[alt]")).some(image => image.getAttribute("alt").trim());
    }

    document.addEventListener("click", event => {
        // Clicks inside the shadow tree are retargeted to the host, so the path is inspected.
        const anchor = findLink(event);
        if (!anchor) return;
        event.preventDefault();
        const href = (anchor.getAttribute("href") || "").trim();
        if (href.startsWith("#")) {
            scrollToFragment(href.slice(1));
            return;
        }
        post({ type: "navigation", uri: anchor.href });
    });

    // Hovering or focusing a link tells the host which link it is, and the host shows the
    // address in its own UI. An empty uri means no link is hovered any more.
    let reportedLink = null;
    // Microsoft Defender Safe Links rewrite every link to one of these hosts and keep the original
    // address in the url query parameter.
    const safeLinksHost = /(^|\.)safelinks\.protection\.(outlook\.com|office365\.us)$/i;

    function findLink(event) {
        return event.composedPath().find(node =>
            node instanceof Element && node.localName === "a" && node.hasAttribute("href"));
    }

    // Returns the address a Safe Links redirect leads to, or "" when the link is not one.
    // Links wrapped more than once are unwrapped up to three times.
    function unwrapSafeLink(href) {
        let current = href;
        let unwrapped = false;
        for (let depth = 0; depth < 3; depth += 1) {
            let parsed;
            try {
                parsed = new URL(current);
            } catch (_) {
                break;
            }
            const target = safeLinksHost.test(parsed.hostname) ? parsed.searchParams.get("url") : null;
            if (!target) break;
            current = target;
            unwrapped = true;
        }
        return unwrapped ? current : "";
    }

    function reportLink(anchor) {
        const href = anchor ? (anchor.getAttribute("href") || "").trim() : "";
        const link = href && !href.startsWith("#") ? anchor : null;
        if (link === reportedLink) return;
        reportedLink = link;
        post(link
            ? { type: "linkHover", uri: link.href, target: unwrapSafeLink(link.href) }
            : { type: "linkHover", uri: "", target: "" });
    }

    // WebView2 does not reliably dispatch mouseover inside the mail's shadow tree, so the pointer
    // is followed with mousemove. Moves within the same link return early.
    document.addEventListener("mousemove", event => reportLink(findLink(event)));
    document.addEventListener("focusin", event => reportLink(findLink(event)));
    document.addEventListener("focusout", () => reportLink(null));
    document.documentElement.addEventListener("mouseleave", () => reportLink(null));
    window.addEventListener("scroll", () => reportLink(null), true);

    window.WinoRenderer = {
        render,
        clear() {
            reportLink(null);
            resetImageFetches();
            remoteContentAllowed = false;
            beginPresentationUpdate();
            window.clearTimeout(imageRefreshTimer);
            clearColorOverrides();
            if (window.WinoMailColors) window.WinoMailColors.resetBackdropCache();
            originalHtml = "";
            schemeRules = [];
            declaresDarkSupport = false;
            adaptsColors = false;
            mailHtmlElement = null;
            mailBodyElement = null;
            fallbackElement = null;
            fallbackText = "";
            fontFaceStyle.textContent = "";
            mailRoot.adoptedStyleSheets = [];
            mailRoot.replaceChildren();
            applyDocumentLanguage("", "");
            applyColorPolicy();
        },
        getOriginalHtml() { return originalHtml; },
        supplyImage,
        getColorPolicy() {
            if (!isDarkTheme || forcedColors.matches) return "original";
            if (invertsColors) return "inverted";
            return declaresDarkSupport && !adaptsColors ? "sender-dark" : "adapted";
        },
        setTheme,
        setTypography,
        setContentInset,
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
