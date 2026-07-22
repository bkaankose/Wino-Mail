(function () {
    "use strict";

    const editor = document.getElementById("wino-editor");
    let lastRange = null;
    let stateTimer = 0;
    let contentTimer = 0;
    let pasteAsHtml = true;
    let darkMode = false;
    let spellCheck = true;

    function post(message) {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(message);
        } else if (window.external && typeof window.external.notify === "function") {
            window.external.notify(JSON.stringify(message));
        }
    }

    function selectionInsideEditor() {
        const selection = window.getSelection();
        if (!selection || selection.rangeCount === 0) {
            return false;
        }

        const range = selection.getRangeAt(0);
        return editor.contains(range.commonAncestorContainer);
    }

    function rememberSelection() {
        if (!selectionInsideEditor()) {
            return;
        }

        lastRange = window.getSelection().getRangeAt(0).cloneRange();
    }

    function restoreSelection() {
        const rangeToRestore = lastRange ? lastRange.cloneRange() : null;
        editor.focus({ preventScroll: true });
        if (!rangeToRestore || !editor.contains(rangeToRestore.commonAncestorContainer)) {
            return false;
        }

        const selection = window.getSelection();
        selection.removeAllRanges();
        selection.addRange(rangeToRestore);
        lastRange = rangeToRestore.cloneRange();
        return true;
    }

    function placeCaretAtStart(node) {
        const range = document.createRange();
        range.selectNodeContents(node);
        range.collapse(true);
        const selection = window.getSelection();
        selection.removeAllRanges();
        selection.addRange(range);
        lastRange = range.cloneRange();
    }

    function decodeBase64(value) {
        const binary = atob(value || "");
        let encoded = "";
        for (let index = 0; index < binary.length; index += 1) {
            encoded += `%${binary.charCodeAt(index).toString(16).padStart(2, "0")}`;
        }
        return decodeURIComponent(encoded);
    }

    function normalizeColor(value) {
        if (!value) {
            return "#000000";
        }

        const match = String(value).match(/rgba?\s*\(\s*(\d+)\D+(\d+)\D+(\d+)/i);
        if (!match) {
            const hex = String(value).trim().toLowerCase();
            if (/^#[0-9a-f]{6}$/.test(hex)) {
                return hex;
            }
            if (/^#[0-9a-f]{3}$/.test(hex)) {
                return `#${hex[1]}${hex[1]}${hex[2]}${hex[2]}${hex[3]}${hex[3]}`;
            }
            return hex;
        }

        return `#${[match[1], match[2], match[3]]
            .map(component => Number(component).toString(16).padStart(2, "0"))
            .join("")}`;
    }

    function currentNode() {
        const selection = window.getSelection();
        if (!selection || selection.rangeCount === 0) {
            return null;
        }
        const node = selection.anchorNode;
        return node && node.nodeType === Node.ELEMENT_NODE ? node : node && node.parentElement;
    }

    function queryState(command) {
        try {
            return document.queryCommandState(command);
        } catch (error) {
            return false;
        }
    }

    function alignment() {
        if (queryState("justifyCenter")) return "center";
        if (queryState("justifyRight")) return "right";
        if (queryState("justifyFull")) return "justify";
        return "left";
    }

    function fontFamily() {
        const value = String(document.queryCommandValue("fontName") || "").trim();
        return value.replace(/^['\"]|['\"]$/g, "");
    }

    function selectionState() {
        const node = currentNode();
        const selection = window.getSelection();
        const computed = node ? window.getComputedStyle(node) : null;
        return {
            bold: queryState("bold"),
            italic: queryState("italic"),
            underline: queryState("underline"),
            strikethrough: queryState("strikeThrough"),
            color: normalizeColor(document.queryCommandValue("foreColor")),
            fontFamily: fontFamily(),
            orderedList: queryState("insertOrderedList"),
            unorderedList: queryState("insertUnorderedList"),
            alignment: alignment(),
            inTable: Boolean(node && node.closest("table")),
            imageSelected: Boolean(window.WinoEditorImages && window.WinoEditorImages.isSelected())
            ,hasSelection: Boolean(selection && !selection.isCollapsed)
            ,selectedText: selection ? selection.toString() : ""
            ,fontSize: computed ? parseInt(computed.fontSize, 10) || null : null
            ,paragraphStyle: node && node.closest("p,h1,h2,h3,h4,h5,h6,pre,blockquote") ? node.closest("p,h1,h2,h3,h4,h5,h6,pre,blockquote").tagName.toLowerCase() : "p"
            ,highlightColor: normalizeColor(document.queryCommandValue("backColor"))
            ,lineHeight: computed ? computed.lineHeight : null
            ,linkUrl: node && node.closest("a") ? node.closest("a").href : null
            ,darkMode
            ,spellCheck
        };
    }

    function sendState() {
        window.clearTimeout(stateTimer);
        stateTimer = window.setTimeout(() => {
            post({ type: "selectionState", state: selectionState() });
        }, 30);
    }

    function sendContentChanged() {
        window.clearTimeout(contentTimer);
        contentTimer = window.setTimeout(() => post({ type: "contentChanged" }), 120);
        sendState();
    }

    function replaceTemporaryFontSizes(pixelSize, existingFonts) {
        const replacements = [];
        const temporaryFonts = Array.from(editor.querySelectorAll('font[size="7"]'))
            .filter(font => !existingFonts.has(font));

        temporaryFonts.forEach(font => {
            const span = document.createElement("span");
            span.style.fontSize = `${pixelSize}px`;
            while (font.firstChild) span.appendChild(font.firstChild);
            font.replaceWith(span);
            replacements.push(span);
        });

        if (replacements.length === 0) {
            return;
        }

        const selection = window.getSelection();
        if (!selection) {
            return;
        }

        const range = document.createRange();
        range.setStart(replacements[0], 0);
        range.setEnd(
            replacements[replacements.length - 1],
            replacements[replacements.length - 1].childNodes.length);
        selection.removeAllRanges();
        selection.addRange(range);
        lastRange = range.cloneRange();
    }

    function exec(command, value) {
        restoreSelection();
        let result = false;

        if (command === "foreColor" || command === "backColor" || command === "hiliteColor" || command === "fontName") {
            document.execCommand("styleWithCSS", false, true);
            result = document.execCommand(command, false, value);
            document.execCommand("styleWithCSS", false, false);
        } else if (command === "fontSize") {
            const existingFonts = new Set(editor.querySelectorAll('font[size="7"]'));
            result = document.execCommand("fontSize", false, "7");
            replaceTemporaryFontSizes(Math.max(8, Math.min(72, Number(value) || 14)), existingFonts);
        } else {
            result = document.execCommand(command, false, value === undefined || value === null ? null : value);
        }

        rememberSelection();
        sendContentChanged();
        return result;
    }

    function createLink(url, text, openInNewWindow) {
        if (!url) return false;
        restoreSelection();
        const selection = window.getSelection();
        if (selection && selection.isCollapsed) {
            const anchor = document.createElement("a");
            anchor.href = url;
            anchor.textContent = text || url;
            if (openInNewWindow) anchor.target = "_blank";
            const range = selection.getRangeAt(0);
            range.insertNode(anchor);
            range.setStartAfter(anchor);
            range.collapse(true);
            selection.removeAllRanges();
            selection.addRange(range);
            rememberSelection();
            sendContentChanged();
            return true;
        }
        const result = exec("createLink", url);
        const linkedNode = currentNode();
        const anchor = linkedNode && linkedNode.closest("a");
        if (anchor && openInNewWindow) anchor.target = "_blank";
        return result;
    }

    function insertHtml(html, range) {
        if (range) {
            const selection = window.getSelection();
            selection.removeAllRanges();
            selection.addRange(range);
            lastRange = range.cloneRange();
        } else {
            restoreSelection();
        }
        const result = document.execCommand("insertHTML", false, html);
        rememberSelection();
        sendContentChanged();
        return result;
    }

    function setContent(base64Html, mode) {
        if (window.WinoEditorImages) window.WinoEditorImages.clearSelection();
        const decoded = decodeBase64(base64Html);
        const html = /<html[\s>]/i.test(decoded)
            ? new DOMParser().parseFromString(decoded, "text/html").body.innerHTML
            : decoded;
        if (mode === "reply") {
            editor.innerHTML = `<p><br></p><p><br></p>${html}`;
        } else {
            editor.innerHTML = html || "<p><br></p>";
        }
        placeCaretAtStart(editor.firstChild || editor);
        sendContentChanged();
        return true;
    }

    function getContent() {
        const clone = editor.cloneNode(true);
        clone.querySelectorAll("[data-wino-editor-artifact]").forEach(node => node.remove());
        clone.querySelectorAll("[data-darkreader-inline-bgcolor],[data-darkreader-inline-color],[data-darkreader-inline-border-top],[data-darkreader-inline-border-right],[data-darkreader-inline-border-bottom],[data-darkreader-inline-border-left]").forEach(node => {
            [...node.attributes].filter(attribute => attribute.name.startsWith("data-darkreader-")).forEach(attribute => node.removeAttribute(attribute.name));
            [...node.style].filter(name => name.startsWith("--darkreader-inline-")).forEach(name => node.style.removeProperty(name));
        });
        return `<html><head><meta charset="utf-8"></head><body>${clone.innerHTML}</body></html>`;
    }

    function getBodyContent() {
        const documentHtml = getContent();
        return new DOMParser().parseFromString(documentHtml, "text/html").body.innerHTML;
    }

    function applyStyle(property, value) {
        restoreSelection();
        const selection = window.getSelection();
        if (!selection || selection.rangeCount === 0) return false;
        const range = selection.getRangeAt(0);
        const element = range.commonAncestorContainer.nodeType === Node.ELEMENT_NODE ? range.commonAncestorContainer : range.commonAncestorContainer.parentElement;
        const target = element && element.closest("p,h1,h2,h3,h4,h5,h6,li,blockquote,div") || element;
        if (!target) return false;
        target.style[property] = value;
        sendContentChanged();
        return true;
    }

    function setTheme(isDark) {
        darkMode = Boolean(isDark);
        document.documentElement.dataset.theme = darkMode ? "dark" : "light";
        if (window.DarkReader) {
            if (darkMode) window.DarkReader.enable({ brightness: 100, contrast: 90, sepia: 0 });
            else window.DarkReader.disable();
        }
        sendState();
    }

    function setTypography(fontFamily, fontSize) {
        editor.style.fontFamily = fontFamily || "Segoe UI";
        editor.style.fontSize = `${Math.max(8, Math.min(72, Number(fontSize) || 14))}px`;
        sendState();
    }

    editor.addEventListener("paste", event => {
        if (pasteAsHtml) return;
        event.preventDefault();
        const text = event.clipboardData ? event.clipboardData.getData("text/plain") : "";
        document.execCommand("insertText", false, text);
    });

    document.execCommand("styleWithCSS", false, false);

    document.addEventListener("selectionchange", () => {
        rememberSelection();
        sendState();
    });
    editor.addEventListener("input", sendContentChanged);
    editor.addEventListener("keyup", sendState);
    editor.addEventListener("mouseup", sendState);
    editor.addEventListener("focus", sendState);

    window.WinoEditor = {
        exec,
        createLink,
        insertHtml,
        setContent,
        getContent,
        getBodyContent,
        restoreSelection,
        rememberSelection,
        notifySelectionChanged: sendState,
        notifyContentChanged: sendContentChanged,
        insertImage(dataUri) {
            return window.WinoEditorImages.insertImage(dataUri);
        },
        insertTable(rows, columns) {
            return window.WinoEditorTables.insertTable(rows, columns);
        },
        tableCommand(command) {
            return window.WinoEditorTables.command(command);
        },
        setTheme,
        setTypography,
        setPasteAsHtml(value) { pasteAsHtml = Boolean(value); },
        setSpellCheck(value) { spellCheck = Boolean(value); editor.spellcheck = spellCheck; sendState(); },
        setParagraphStyle(tag) { return exec("formatBlock", tag || "p"); },
        setLineHeight(value) { return applyStyle("lineHeight", value || "normal"); },
        insertEmoji(value) { return insertHtml(String(value || "")); },
        focus() {
            restoreSelection();
        }
    };

    function announceReady() {
        document.removeEventListener("DOMContentLoaded", announceReady);
        post({ type: "ready" });
        sendState();
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", announceReady);
    } else {
        announceReady();
    }
}());
