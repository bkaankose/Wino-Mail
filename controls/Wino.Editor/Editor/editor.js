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
    let autoCorrect = false;
    let correctionRevision = 0;
    let correctionRequestId = 0;
    const pendingCorrections = new Map();
    let correctionHighlightTimer = 0;
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

    const sanitizeOptions = {
        USE_PROFILES: { html: true },
        ADD_ATTR: ["target"],
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

    // Every HTML string that enters the editable surface passes through DOMPurify first.
    // Drafts, replies, templates, signatures and pasted content can all carry mail markup.
    function sanitizeHtml(html) {
        if (!window.DOMPurify || window.DOMPurify.isSupported !== true ||
            typeof window.DOMPurify.sanitize !== "function") {
            throw new Error("DOMPurify is unavailable; refusing to insert untrusted HTML.");
        }
        const sanitized = window.DOMPurify.sanitize(String(html || ""), sanitizeOptions);
        if (typeof sanitized !== "string") {
            throw new Error("DOMPurify returned an unexpected result; refusing to insert untrusted HTML.");
        }
        return sanitized;
    }

    // Dark mode adapts colors for display only. Each adapted element carries an id attribute
    // that a generated stylesheet targets; inline styles stay exactly as authored, and
    // getContent() removes the ids, so the sent HTML never contains dark-mode colors.
    const composeRootAttribute = "data-wino-compose-root";
    const darkColorAttribute = "data-wino-dk";
    const darkColorStyle = document.createElement("style");
    darkColorStyle.id = "wino-dark-colors";
    document.head.appendChild(darkColorStyle);
    let darkColorTimer = 0;
    let nextDarkColorId = 1;

    function refreshDarkColors() {
        window.clearTimeout(darkColorTimer);
        darkColorStyle.textContent = "";
        const byElement = new Map();
        if (darkMode && window.WinoMailColors) {
            const surface = getComputedStyle(editor).backgroundColor;
            window.WinoMailColors.computeDarkOverrides(editor, surface).forEach(override => {
                if (override.element === editor) return;
                let declarations = byElement.get(override.element);
                if (!declarations) byElement.set(override.element, declarations = []);
                declarations.push(`${override.property}: ${override.value} !important;`);
            });
        }

        editor.querySelectorAll(`[${darkColorAttribute}]`).forEach(element => {
            if (!byElement.has(element)) element.removeAttribute(darkColorAttribute);
        });

        const usedIds = new Set();
        const rules = [];
        byElement.forEach((declarations, element) => {
            let id = element.getAttribute(darkColorAttribute);
            if (!id || usedIds.has(id)) {
                id = String(nextDarkColorId++);
                element.setAttribute(darkColorAttribute, id);
            }
            usedIds.add(id);
            rules.push(`#wino-editor [${darkColorAttribute}="${id}"] { ${declarations.join(" ")} }`);
        });
        darkColorStyle.textContent = rules.join("\n");
    }

    function scheduleDarkColorRefresh() {
        if (!darkMode) return;
        window.clearTimeout(darkColorTimer);
        darkColorTimer = window.setTimeout(refreshDarkColors, 250);
    }

    let applicationShortcuts = [];

    function setApplicationShortcuts(shortcuts) {
        const protectedKeys = new Set(["a", "b", "c", "f", "i", "k", "n", "o", "p", "r", "s", "u", "v", "w", "x", "y", "z", "tab", "delete", "backspace"]);
        applicationShortcuts = Array.isArray(shortcuts)
            ? shortcuts.filter(shortcut => shortcut && shortcut.control === true &&
                typeof shortcut.key === "string" && !protectedKeys.has(shortcut.key.toLowerCase()))
            : [];
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
            result = execStyledCommand(command, value);
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

    function execStyledCommand(command, value) {
        const commands = command === "backColor" || command === "hiliteColor"
            ? ["hiliteColor", "backColor"]
            : [command];
        let result = false;

        document.execCommand("styleWithCSS", false, true);
        try {
            for (const candidate of commands) {
                result = document.execCommand(candidate, false, value);
                if (result) break;
            }
        } finally {
            document.execCommand("styleWithCSS", false, false);
        }

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

        const result = execStyledCommand(command, sentinel);

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
        const result = document.execCommand("insertHTML", false, sanitizeHtml(html));
        rememberSelection();
        sendContentChanged();
        return result;
    }

    function setContent(base64Html, mode) {
        if (window.WinoEditorImages) window.WinoEditorImages.clearSelection();
        const decoded = decodeBase64(base64Html);
        // DOMPurify returns the body content of a full document, like the parser used to.
        const html = unwrapComposeRoot(sanitizeHtml(decoded));
        if (mode === "reply") {
            editor.innerHTML = `<p><br></p><p><br></p>${html}`;
        } else {
            editor.innerHTML = html || "<p><br></p>";
        }
        placeCaretAtStart(editor.firstChild || editor);
        refreshDarkColors();
        sendContentChanged();
        return true;
    }

    // getContent() wraps the body in a root that carries the composer typography. Reopening a
    // draft or signature removes that root again, so wrappers never nest.
    function unwrapComposeRoot(html) {
        if (!html || html.indexOf(composeRootAttribute) < 0) return html;
        const parsed = new DOMParser().parseFromString(html, "text/html");
        const root = parsed.body.firstElementChild;
        if (!root || parsed.body.children.length !== 1 || !root.hasAttribute(composeRootAttribute) ||
            parsed.body.textContent.trim() !== root.textContent.trim()) {
            return html;
        }
        return root.innerHTML;
    }

    function toHex(match, red, green, blue, alpha) {
        if (alpha !== undefined && parseFloat(alpha) < 1) return match;
        return `#${[red, green, blue].map(value => Math.min(255, Number(value)).toString(16).padStart(2, "0")).join("")}`;
    }

    // Mail clients disagree on default list and quote spacing. Inline what the author saw.
    function inlineBlockSpacing(source, target) {
        const selector = "ul, ol, blockquote";
        const sourceBlocks = source.querySelectorAll(selector);
        const targetBlocks = target.querySelectorAll(selector);
        if (sourceBlocks.length !== targetBlocks.length) return;
        targetBlocks.forEach((block, index) => {
            const style = block.style;
            if (style.margin || style.marginTop || style.marginBottom || style.marginLeft || style.marginRight) return;
            const computed = getComputedStyle(sourceBlocks[index]);
            style.margin = `${computed.marginTop} ${computed.marginRight} ${computed.marginBottom} ${computed.marginLeft}`;
            if (block.localName !== "blockquote" && !style.paddingLeft && !style.paddingInlineStart) {
                style.paddingLeft = computed.paddingLeft;
            }
        });
    }

    function escapeAttribute(value) {
        return String(value).replace(/&/g, "&amp;").replace(/"/g, "&quot;").replace(/</g, "&lt;");
    }

    function getContent() {
        const clone = editor.cloneNode(true);
        inlineBlockSpacing(editor, clone);
        clone.querySelectorAll("[data-wino-editor-artifact]").forEach(node => node.remove());
        clone.querySelectorAll("*").forEach(node => {
            [...node.attributes]
                .filter(attribute => attribute.name === darkColorAttribute || attribute.name.startsWith("data-darkreader-"))
                .forEach(attribute => node.removeAttribute(attribute.name));
            if (!node.hasAttribute("style")) return;
            // Drafts saved by the old Dark Reader integration can still carry its variables.
            [...node.style].filter(name => name.startsWith("--darkreader-")).forEach(name => node.style.removeProperty(name));
            const style = node.getAttribute("style") || "";
            const normalized = style.replace(/rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*([\d.]+)\s*)?\)/gi, toHex).trim();
            if (!normalized) node.removeAttribute("style");
            else if (normalized !== style) node.setAttribute("style", normalized);
        });

        const declarations = [];
        if (editor.style.fontFamily) declarations.push(`font-family: ${editor.style.fontFamily}`);
        if (editor.style.fontSize) declarations.push(`font-size: ${editor.style.fontSize}`);
        const body = declarations.length
            ? `<div ${composeRootAttribute}="true" style="${escapeAttribute(declarations.join("; "))}">${clone.innerHTML}</div>`
            : clone.innerHTML;
        return `<html><head><meta charset="utf-8"></head><body>${body}</body></html>`;
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
        refreshDarkColors();
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
        container.innerHTML = sanitizeHtml(html);
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
        if (event.repeat) return;

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
            return;
        }

        const applicationShortcut = applicationShortcuts.find(shortcut =>
            shortcut.key.toLowerCase() === event.key.toLowerCase() &&
            shortcut.control === event.ctrlKey &&
            shortcut.alt === event.altKey &&
            shortcut.shift === event.shiftKey);
        if (!applicationShortcut) return;

        event.preventDefault();
        event.stopPropagation();
        post({ type: "applicationShortcut", gesture: applicationShortcut });
    }, true);
    document.addEventListener("keyup", event => {
        if (event.key.toLowerCase() === "v") pasteAsPlainTextOnce = false;
    }, true);

    document.addEventListener("selectionchange", () => {
        rememberSelection();
        sendState();
        window.setTimeout(updateLinkBubble, 0);
    });
    function rangeInBlock(block, start, length) {
        const walker = document.createTreeWalker(block, NodeFilter.SHOW_TEXT);
        let node;
        let offset = 0;
        let first = null;
        let last = null;
        while ((node = walker.nextNode())) {
            const end = offset + node.length;
            if (!first && start >= offset && start <= end) first = { node, offset: start - offset };
            if (first && start + length >= offset && start + length <= end) {
                last = { node, offset: start + length - offset };
                break;
            }
            offset = end;
        }
        if (!first || !last) return null;
        const range = document.createRange();
        range.setStart(first.node, first.offset);
        range.setEnd(last.node, last.offset);
        return range;
    }

    function requestAutoCorrection() {
        const selection = window.getSelection();
        if (!selection || !selection.isCollapsed || selection.rangeCount === 0 ||
            selection.anchorNode?.nodeType !== Node.TEXT_NODE) return;
        const node = selection.anchorNode;
        const before = node.textContent.slice(0, selection.anchorOffset);
        const match = before.match(/([\p{L}][\p{L}\p{M}'’-]{1,63})[\s.,;:!?]$/u);
        if (!match || node.parentElement?.closest("a, code, pre, [contenteditable='false']")) return;
        const tokenBefore = before.slice(0, -1).split(/\s/).pop() || "";
        if (/[@:/\\]/.test(tokenBefore)) return;
        const block = node.parentElement?.closest("p, div, li, h1, h2, h3, blockquote") || editor;
        if (!editor.contains(block) && block !== editor) return;
        const prefix = document.createRange();
        prefix.setStart(block, 0);
        prefix.setEnd(node, selection.anchorOffset);
        const caretOffset = prefix.toString().length;
        const requestId = ++correctionRequestId;
        pendingCorrections.clear();
        pendingCorrections.set(requestId, { block, caretOffset, revision: correctionRevision, word: match[1] });
        post({ type: "autoCorrect", requestId, word: match[1] });
    }

    function applyAutoCorrection(requestId, original, replacement) {
        const pending = pendingCorrections.get(requestId);
        pendingCorrections.delete(requestId);
        if (!pending || !autoCorrect || !spellCheck || pending.revision !== correctionRevision ||
            pending.word !== original || !pending.block.isConnected || !replacement ||
            /[\r\n<>]/.test(replacement)) return false;
        const selection = window.getSelection();
        if (!selection || !selection.isCollapsed || !pending.block.contains(selection.anchorNode)) return false;
        const wordStart = pending.caretOffset - original.length - 1;
        const target = rangeInBlock(pending.block, wordStart, original.length);
        if (!target || target.toString() !== original) return false;
        if (target.startContainer.parentElement?.closest("a, code, pre")) return false;
        const caretPrefix = document.createRange();
        caretPrefix.setStart(pending.block, 0);
        caretPrefix.setEnd(selection.anchorNode, selection.anchorOffset);
        if (caretPrefix.toString().length !== pending.caretOffset) return false;
        selection.removeAllRanges();
        selection.addRange(target);
        if (!document.execCommand("insertText", false, replacement)) return false;
        const newCaret = rangeInBlock(pending.block, pending.caretOffset + replacement.length - original.length, 0);
        if (newCaret) {
            selection.removeAllRanges();
            selection.addRange(newCaret);
        }
        correctionRevision++;
        if (window.CSS?.highlights && window.Highlight) {
            const painted = rangeInBlock(pending.block, wordStart, replacement.length);
            if (painted) {
                CSS.highlights.set("wino-auto-correction", new Highlight(painted));
                clearTimeout(correctionHighlightTimer);
                correctionHighlightTimer = window.setTimeout(() => CSS.highlights.delete("wino-auto-correction"), 850);
            }
        }
        sendContentChanged();
        return true;
    }

    editor.addEventListener("compositionstart", () => { correctionRevision++; });
    editor.addEventListener("input", event => {
        correctionRevision++;
        if (!/^(insertText|insertCompositionText|deleteContent)/.test(event.inputType || "")) {
            scheduleDarkColorRefresh();
        }
        if (autoCorrect && spellCheck && event.inputType === "insertText" &&
            /^[\s.,;:!?]$/.test(event.data || "") && !event.isComposing) {
            requestAutoCorrection();
        }
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
        setSpellCheck(value) { spellCheck = Boolean(value); editor.spellcheck = spellCheck; correctionRevision++; sendState(); },
        setAutoCorrect(value) { autoCorrect = Boolean(value); correctionRevision++; pendingCorrections.clear(); },
        applyAutoCorrection,
        setSpellCheckLanguage(value) {
            const languageCode = String(value || "").trim();
            document.documentElement.lang = languageCode;
            editor.lang = languageCode;
            correctionRevision++;
        },
        setApplicationShortcuts,
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
