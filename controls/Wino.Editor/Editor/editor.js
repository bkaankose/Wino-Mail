(function () {
    "use strict";

    const editor = document.getElementById("wino-editor");
    let lastRange = null;
    let stateTimer = 0;
    let contentTimer = 0;
    let pasteAsHtml = true;
    let pasteAsPlainTextOnce = false;
    let darkMode = false;
    let spellCheck = true;
    let displayedLink = null;
    const codeBlockClass = "wino-code-block";
    const codeBlockStyles = {
        margin: "0.5em 0",
        padding: "10px 12px",
        border: "1px solid #d6d6d6",
        borderRadius: "4px",
        color: "#1f1f1f",
        backgroundColor: "#f5f5f5",
        fontFamily: 'Consolas, "Courier New", monospace',
        fontSize: "0.95em",
        lineHeight: "1.45",
        whiteSpace: "pre-wrap",
        overflowWrap: "anywhere"
    };
    const linkBubble = document.createElement("button");
    linkBubble.type = "button";
    linkBubble.className = "wino-link-bubble";
    linkBubble.dataset.winoEditorArtifact = "true";
    linkBubble.textContent = "Remove link";
    document.getElementById("wino-overlay").appendChild(linkBubble);

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
            return "";
        }

        const colorValue = String(value);
        const components = colorValue.match(/[\d.]+%?/g) || [];
        if (/^rgba/i.test(colorValue) && components.length >= 4 && parseFloat(components[3]) === 0) {
            return "";
        }

        const match = colorValue.match(/rgba?\s*\(\s*(\d+)\D+(\d+)\D+(\d+)/i);
        if (!match) {
            const hex = colorValue.trim().toLowerCase();
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

    function selectionColor(property, legacyAttribute) {
        let element = currentNode();
        while (element && element !== editor) {
            const value = element.style && element.style.getPropertyValue(property) ||
                element.getAttribute && element.getAttribute(legacyAttribute);
            if (value) return normalizeColor(value);
            element = element.parentElement;
        }

        return "";
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

    function fontFamily(computed) {
        const value = String(document.queryCommandValue("fontName") || "").trim();
        const computedValue = computed ? String(computed.fontFamily || "").split(",")[0].trim() : "";
        return (computedValue || value).replace(/^['\"]|['\"]$/g, "");
    }

    function paragraphStyle(node) {
        const block = node && node.closest("p,h1,h2,h3,h4,h5,h6,pre,blockquote");
        if (!block) return "p";
        return block.classList.contains(codeBlockClass) ? "code" : block.tagName.toLowerCase();
    }

    function selectionState() {
        const node = currentNode();
        const selection = window.getSelection();
        const computed = node ? window.getComputedStyle(node) : null;
        const imageProperties = window.WinoEditorImages && window.WinoEditorImages.getSelectedProperties
            ? window.WinoEditorImages.getSelectedProperties()
            : null;
        return {
            bold: queryState("bold"),
            italic: queryState("italic"),
            underline: queryState("underline"),
            strikethrough: queryState("strikeThrough"),
            color: selectionColor("color", "color"),
            fontFamily: fontFamily(computed),
            orderedList: queryState("insertOrderedList"),
            unorderedList: queryState("insertUnorderedList"),
            alignment: alignment(),
            inTable: Boolean(node && node.closest("table")),
            imageSelected: Boolean(imageProperties)
            ,imageAltText: imageProperties ? imageProperties.altText : null
            ,imageLinkUrl: imageProperties ? imageProperties.linkUrl : null
            ,hasSelection: Boolean(selection && !selection.isCollapsed)
            ,selectedText: selection ? selection.toString() : ""
            ,fontSize: computed ? parseInt(computed.fontSize, 10) || null : null
            ,paragraphStyle: paragraphStyle(node)
            ,highlightColor: selectionColor("background-color", "bgcolor")
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

    function currentLink() {
        const node = currentNode();
        return node && node.closest ? node.closest("a") : null;
    }

    function hideLinkBubble() {
        displayedLink = null;
        linkBubble.classList.remove("is-visible");
        linkBubble.removeAttribute("aria-label");
    }

    function updateLinkBubble() {
        const anchor = currentLink();
        if (!anchor || !editor.contains(anchor)) {
            hideLinkBubble();
            return;
        }

        const rectangle = anchor.getBoundingClientRect();
        if (rectangle.width === 0 && rectangle.height === 0) {
            hideLinkBubble();
            return;
        }

        displayedLink = anchor;
        linkBubble.classList.add("is-visible");
        linkBubble.style.left = `${Math.max(8, Math.min(window.innerWidth - linkBubble.offsetWidth - 8, rectangle.left))}px`;
        const preferredTop = rectangle.bottom + 6;
        const fallbackTop = rectangle.top - linkBubble.offsetHeight - 6;
        linkBubble.style.top = `${Math.max(8, preferredTop + linkBubble.offsetHeight <= window.innerHeight - 8
            ? preferredTop
            : fallbackTop)}px`;
        linkBubble.setAttribute("aria-label", `Remove link ${anchor.href}`);
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

        if ((command === "foreColor" || command === "backColor" || command === "hiliteColor") && !value) {
            return clearColor(command);
        }

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

    function hasExplicitColor(element, property, legacyAttribute) {
        return Boolean(element && element.style && element.style.getPropertyValue(property)) ||
            Boolean(legacyAttribute && element && element.hasAttribute && element.hasAttribute(legacyAttribute));
    }

    function splitAncestorAroundNode(node, ancestor) {
        if (!node || !ancestor || !ancestor.parentNode || !ancestor.contains(node)) return;

        const beforeRange = document.createRange();
        beforeRange.selectNodeContents(ancestor);
        beforeRange.setEndBefore(node);

        const afterRange = document.createRange();
        afterRange.selectNodeContents(ancestor);
        afterRange.setStartAfter(node);

        const before = ancestor.cloneNode(false);
        before.appendChild(beforeRange.cloneContents());
        const after = ancestor.cloneNode(false);
        after.appendChild(afterRange.cloneContents());
        const parent = ancestor.parentNode;

        if (before.hasChildNodes()) parent.insertBefore(before, ancestor);
        parent.insertBefore(node, ancestor);
        if (after.hasChildNodes()) parent.insertBefore(after, ancestor);
        ancestor.remove();
    }

    function removeEmptyStyle(element) {
        if (element.hasAttribute("style") && !element.getAttribute("style").trim()) {
            element.removeAttribute("style");
        }

        if (element.tagName === "SPAN" && element.attributes.length === 0) {
            element.replaceWith(...element.childNodes);
        }
    }

    function clearColor(command) {
        restoreSelection();
        const selection = window.getSelection();
        if (!selection || selection.rangeCount === 0) return false;

        const isTextColor = command === "foreColor";
        const property = isTextColor ? "color" : "background-color";
        const legacyAttribute = isTextColor ? "color" : "bgcolor";
        const sentinel = isTextColor ? "rgb(1, 2, 3)" : "rgb(4, 5, 6)";
        const previousValues = new Map(Array.from(editor.querySelectorAll("*")).map(element => [
            element,
            element.style ? element.style.getPropertyValue(property) : ""
        ]));

        document.execCommand("styleWithCSS", false, true);
        const result = document.execCommand(command, false, sentinel);
        document.execCommand("styleWithCSS", false, false);

        const markedElements = Array.from(editor.querySelectorAll("*")).filter(element =>
            normalizeColor(element.style && element.style.getPropertyValue(property)) === normalizeColor(sentinel) &&
            previousValues.get(element) !== element.style.getPropertyValue(property));

        markedElements.forEach(element => {
            const coloredAncestors = [];
            let ancestor = element.parentElement;
            while (ancestor && ancestor !== editor) {
                if (hasExplicitColor(ancestor, property, legacyAttribute)) coloredAncestors.push(ancestor);
                ancestor = ancestor.parentElement;
            }

            coloredAncestors.forEach(coloredAncestor => splitAncestorAroundNode(element, coloredAncestor));
            element.style.removeProperty(property);
            element.removeAttribute(legacyAttribute);
            removeEmptyStyle(element);
        });

        rememberSelection();
        sendContentChanged();
        return result || markedElements.length > 0;
    }

    function createLink(url, text, openInNewWindow) {
        if (!url) return false;
        restoreSelection();
        const existingAnchor = currentLink();
        if (existingAnchor) {
            existingAnchor.href = url;
            if (text) existingAnchor.textContent = text;
            if (openInNewWindow) {
                existingAnchor.target = "_blank";
                existingAnchor.rel = "noopener noreferrer";
            } else {
                existingAnchor.removeAttribute("target");
                existingAnchor.removeAttribute("rel");
            }
            sendContentChanged();
            return true;
        }

        const selection = window.getSelection();
        if (selection && selection.isCollapsed) {
            const anchor = document.createElement("a");
            anchor.href = url;
            anchor.textContent = text || url;
            if (openInNewWindow) {
                anchor.target = "_blank";
                anchor.rel = "noopener noreferrer";
            }
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

    function removeLink(anchorOverride) {
        restoreSelection();
        const anchor = anchorOverride || currentLink() || displayedLink;
        if (!anchor) return false;

        const parent = anchor.parentNode;
        const firstChild = anchor.firstChild;
        const lastChild = anchor.lastChild;
        while (anchor.firstChild) parent.insertBefore(anchor.firstChild, anchor);
        anchor.remove();

        if (firstChild && lastChild) {
            const range = document.createRange();
            range.setStartBefore(firstChild);
            range.setEndAfter(lastChild);
            range.collapse(false);
            const selection = window.getSelection();
            selection.removeAllRanges();
            selection.addRange(range);
            lastRange = range.cloneRange();
        }

        hideLinkBubble();
        sendContentChanged();
        return true;
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
        editor.style.fontFamily = fontFamily || "Calibri";
        editor.style.fontSize = `${Math.max(8, Math.min(72, Number(fontSize) || 14))}px`;
        sendState();
    }

    function clearFormatting() {
        return exec("removeFormat");
    }

    function setParagraphStyle(tag) {
        restoreSelection();
        const currentBlock = currentNode() && currentNode().closest("p,h1,h2,h3,h4,h5,h6,pre,blockquote");
        if (currentBlock && currentBlock.classList.contains(codeBlockClass)) {
            currentBlock.classList.remove(codeBlockClass);
            Object.keys(codeBlockStyles).forEach(property => currentBlock.style[property] = "");
            if (!currentBlock.className) currentBlock.removeAttribute("class");
            if (!currentBlock.getAttribute("style")) currentBlock.removeAttribute("style");
        }

        const requestedTag = String(tag || "p").toLowerCase();
        const result = exec("formatBlock", requestedTag === "code" ? "pre" : requestedTag);
        if (requestedTag !== "code") return result;

        const codeBlock = currentNode() && currentNode().closest("pre");
        if (!codeBlock) return result;
        codeBlock.classList.add(codeBlockClass);
        Object.assign(codeBlock.style, codeBlockStyles);
        rememberSelection();
        sendContentChanged();
        return true;
    }

    function lineBreakOffsets(value) {
        const offsets = [];
        let textOffset = 0;
        String(value || "").replace(/\r\n?/g, "\n").split("").forEach(character => {
            if (character === "\n") offsets.push(textOffset);
            else textOffset += 1;
        });
        return offsets;
    }

    function textWithoutLineBreaks(value) {
        return String(value || "").replace(/\r\n?/g, "\n").replace(/\n/g, "");
    }

    function structuralLineBreakOffsets(container) {
        const offsets = [];
        let textOffset = 0;
        const blockTags = /^(DIV|P|LI|BLOCKQUOTE|H[1-6]|PRE|TABLE|TR)$/;

        function visit(parent) {
            const children = Array.from(parent.childNodes);
            children.forEach((child, index) => {
                if (child.nodeType === Node.TEXT_NODE) {
                    textOffset += textWithoutLineBreaks(child.nodeValue).length;
                    return;
                }

                if (child.nodeType !== Node.ELEMENT_NODE) return;
                if (child.tagName === "BR") {
                    offsets.push(textOffset);
                    return;
                }

                const isBlock = blockTags.test(child.tagName);
                if (isBlock && textOffset > 0) offsets.push(textOffset);
                visit(child);
                const nextElement = children[index + 1];
                if (isBlock && nextElement && nextElement.nodeType !== Node.ELEMENT_NODE) {
                    offsets.push(textOffset);
                }
            });
        }

        visit(container);
        return offsets;
    }

    function insertBreakAtTextOffset(container, targetOffset) {
        const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
        let currentOffset = 0;
        let textNode = walker.nextNode();

        while (textNode) {
            const compactValue = textWithoutLineBreaks(textNode.nodeValue);
            const nextOffset = currentOffset + compactValue.length;
            if (targetOffset <= nextOffset) {
                const localOffset = Math.max(0, targetOffset - currentOffset);
                let rawOffset = 0;
                let compactOffset = 0;
                while (rawOffset < textNode.nodeValue.length && compactOffset < localOffset) {
                    const character = textNode.nodeValue[rawOffset];
                    rawOffset += 1;
                    if (character !== "\r" && character !== "\n") compactOffset += 1;
                }
                const breakElement = document.createElement("br");
                if (localOffset === 0) {
                    let boundary = textNode;
                    while (boundary.parentNode !== container && boundary.parentElement &&
                        !/^(DIV|P|LI|BLOCKQUOTE|H[1-6]|PRE)$/.test(boundary.parentElement.tagName)) {
                        boundary = boundary.parentElement;
                    }
                    boundary.parentNode.insertBefore(breakElement, boundary);
                } else if (localOffset === compactValue.length) {
                    let boundary = textNode;
                    while (boundary.parentNode !== container && boundary.parentElement &&
                        !/^(DIV|P|LI|BLOCKQUOTE|H[1-6]|PRE)$/.test(boundary.parentElement.tagName)) {
                        boundary = boundary.parentElement;
                    }
                    boundary.parentNode.insertBefore(breakElement, boundary.nextSibling);
                } else {
                    const tail = textNode.splitText(rawOffset);
                    tail.parentNode.insertBefore(breakElement, tail);
                }
                return true;
            }
            currentOffset = nextOffset;
            textNode = walker.nextNode();
        }

        return false;
    }

    function normalizePastedHtml(html, plainText) {
        const container = document.createElement("div");
        container.innerHTML = html || "";
        container.querySelectorAll("script,style,link,meta,iframe,object,embed").forEach(node => node.remove());
        container.querySelectorAll("*").forEach(node => {
            Array.from(node.attributes).forEach(attribute => {
                if (/^on/i.test(attribute.name)) node.removeAttribute(attribute.name);
            });
            if (node.tagName === "A" && node.hasAttribute("href") &&
                !/^(https?|mailto|ftp):/i.test(node.getAttribute("href").trim())) {
                node.removeAttribute("href");
            }
        });

        const normalizedPlainText = String(plainText || "").replace(/\r\n?/g, "\n");
        const domText = container.textContent || "";
        if (normalizedPlainText.includes("\n") &&
            textWithoutLineBreaks(domText) === textWithoutLineBreaks(normalizedPlainText)) {
            const existingOffsets = new Set(structuralLineBreakOffsets(container));
            lineBreakOffsets(normalizedPlainText)
                .filter(offset => !existingOffsets.has(offset))
                .sort((left, right) => right - left)
                .forEach(offset => insertBreakAtTextOffset(container, offset));
            const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
            let textNode = walker.nextNode();
            while (textNode) {
                textNode.nodeValue = textNode.nodeValue.replace(/\r\n?|\n/g, "");
                textNode = walker.nextNode();
            }
        }

        return container.innerHTML;
    }

    function linkify(block, convertLineBreaks) {
        if (!block || typeof linkifyElement !== "function") return;

        linkifyElement(block, {
            defaultProtocol: "https",
            nl2br: Boolean(convertLineBreaks),
            target: "_blank",
            rel: "noopener noreferrer",
            ignoreTags: ["A", "CODE", "PRE", "SCRIPT", "STYLE"],
            attributes: { "data-wino-auto-link": "true" }
        });
    }

    function linkifyHtml(html, plainText) {
        const container = document.createElement("div");
        if (html) {
            container.innerHTML = html;
        } else {
            container.textContent = plainText || "";
        }

        linkify(container, !html);
        return container.innerHTML;
    }

    function linkifyBlock(block) {
        if (!block || typeof linkifyElement !== "function") return;

        const selection = window.getSelection();
        let marker = null;
        if (selection && selection.rangeCount > 0 && selection.isCollapsed &&
            block.contains(selection.getRangeAt(0).commonAncestorContainer)) {
            marker = document.createElement("span");
            marker.dataset.winoEditorArtifact = "true";
            const range = selection.getRangeAt(0);
            range.insertNode(marker);
        }

        linkify(block);

        if (marker && marker.isConnected) {
            const range = document.createRange();
            range.setStartBefore(marker);
            range.collapse(true);
            selection.removeAllRanges();
            selection.addRange(range);
            marker.remove();
            lastRange = range.cloneRange();
        }
    }

    function linkifyAroundSelection(includePreviousBlock) {
        const node = currentNode();
        const block = node && node.closest
            ? node.closest("p,div,li,blockquote,h1,h2,h3,h4,h5,h6") || editor
            : editor;
        if (includePreviousBlock && block !== editor && block.previousElementSibling) {
            linkifyBlock(block.previousElementSibling);
        }
        linkifyBlock(block);
    }

    editor.addEventListener("paste", event => {
        const hasImageFiles = event.clipboardData &&
            Array.from(event.clipboardData.files || []).some(file => file.type.startsWith("image/"));
        if (hasImageFiles && !pasteAsPlainTextOnce) return;

        event.preventDefault();
        const text = event.clipboardData ? event.clipboardData.getData("text/plain") : "";
        const clipboardHtml = event.clipboardData ? event.clipboardData.getData("text/html") : "";
        const shouldPasteHtml = pasteAsHtml && !pasteAsPlainTextOnce && clipboardHtml;
        pasteAsPlainTextOnce = false;
        if (shouldPasteHtml) {
            const normalizedHtml = normalizePastedHtml(clipboardHtml, text);
            document.execCommand("insertHTML", false, linkifyHtml(normalizedHtml, text));
        } else {
            document.execCommand("insertHTML", false, linkifyHtml(null, text));
        }
        rememberSelection();
        sendContentChanged();
    });

    document.execCommand("styleWithCSS", false, false);

    document.addEventListener("keydown", event => {
        if (event.key === "Escape" && linkBubble.classList.contains("is-visible")) {
            event.preventDefault();
            event.stopPropagation();
            hideLinkBubble();
            return;
        }

        if (!(event.ctrlKey || event.metaKey)) return;

        if (event.key.toLowerCase() === "k") {
            event.preventDefault();
            rememberSelection();
            post({ type: "shortcut", command: "openLinkDialog" });
            return;
        }

        if (event.shiftKey && event.key.toLowerCase() === "v") {
            pasteAsPlainTextOnce = true;
            return;
        }

        if (event.key === "\\") {
            event.preventDefault();
            clearFormatting();
        }
    }, true);
    document.addEventListener("keyup", event => {
        if (event.key.toLowerCase() === "v") pasteAsPlainTextOnce = false;
    }, true);

    document.addEventListener("selectionchange", () => {
        rememberSelection();
        sendState();
        window.setTimeout(updateLinkBubble, 0);
    });
    editor.addEventListener("input", event => {
        const isLinkBoundary = event.inputType === "insertParagraph" ||
            event.inputType === "insertLineBreak" ||
            event.inputType === "insertFromPaste" ||
            (event.inputType === "insertText" && /\s/.test(event.data || ""));
        if (isLinkBoundary) {
            linkifyAroundSelection(
                event.inputType === "insertParagraph" || event.inputType === "insertLineBreak");
        }
        sendContentChanged();
    });
    editor.addEventListener("keyup", sendState);
    editor.addEventListener("mouseup", () => { sendState(); updateLinkBubble(); });
    editor.addEventListener("focus", sendState);
    editor.addEventListener("click", event => {
        const anchor = event.target && event.target.closest ? event.target.closest("a") : null;
        if (!anchor || !editor.contains(anchor)) return;

        event.preventDefault();
        if ((event.ctrlKey || event.metaKey) && anchor.href) {
            post({ type: "openLink", url: anchor.href });
            hideLinkBubble();
        } else {
            window.setTimeout(updateLinkBubble, 0);
        }
    });
    editor.addEventListener("scroll", () => updateLinkBubble());
    window.addEventListener("resize", () => updateLinkBubble());
    linkBubble.addEventListener("pointerdown", event => event.preventDefault());
    linkBubble.addEventListener("click", event => {
        event.preventDefault();
        removeLink(displayedLink);
    });

    window.WinoEditor = {
        exec,
        createLink,
        removeLink,
        clearFormatting,
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
        setParagraphStyle,
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
