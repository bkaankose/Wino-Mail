const joditConfig = {
    "useSearch": false,
    "toolbar": true,
    "buttons": "bold,italic,underline,strikethrough,brush,ul,ol,font,fontsize,paragraph,image,link,indent,outdent,align,lineHeight,table",
    "inline": true,
    "toolbarAdaptive": false,
    "toolbarInlineForSelection": false,
    "showCharsCounter": false,
    "showWordsCounter": false,
    "showXPathInStatusbar": false,
    "spellcheck": true,
    "link": {
        "processVideoLink": false
    },
    "disablePlugins": "add-new-line,backspace",
    "showPlaceholder": false,
    "uploader": {
        "insertImageAsBase64URI": true
    },
    "enter": "DIV"
};

let editor;
let editorDomObserver;
let selectionChangeHandler;
let stateSyncQueued = false;
let imageInputBound = false;
let inlineFontsPluginRegistered = false;
let lastKnownRange = null;
let availableTextColors = [];
let availableHighlightColors = [];

function initializeJodit(fonts, defaultComposerFont, defaultComposerFontSize, defaultReaderFont, defaultReaderFontSize, textColors, highlightColors) {
    if (editor) {
        scheduleStateSync();
        return true;
    }

    availableTextColors = Array.isArray(textColors) ? textColors.map(normalizeColor).filter(Boolean) : [];
    availableHighlightColors = Array.isArray(highlightColors) ? highlightColors.map(normalizeColor).filter(Boolean) : [];

    const fontsWithFallbackObject = fonts.reduce((acc, font) => {
        acc[`'${font}',Arial,sans-serif`] = font;
        return acc;
    }, {});

    const mergedConfig = {
        ...joditConfig,
        controls: {
            font: {
                list: Jodit.atom(fontsWithFallbackObject)
            }
        },
        style: { font: `${defaultReaderFontSize}px ${defaultReaderFont}` }
    };

    if (!inlineFontsPluginRegistered) {
        Jodit.plugins.add('inlineFonts', jodit => {
            jodit.events.on('afterEnter', () => {
                const current = getSelectionElement();
                if (!current) {
                    return;
                }

                current.style.fontFamily = `'${defaultComposerFont}',Arial,sans-serif`;
                current.style.fontSize = `${defaultComposerFontSize}px`;
                rememberSelection();
                scheduleStateSync();
            });
        });

        inlineFontsPluginRegistered = true;
    }

    editor = Jodit.make('#editor', mergedConfig);

    bindImageInput();
    bindEditorStateTracking();
    toggleToolbar(false);
    scheduleStateSync();

    return true;
}

function RenderHTML(htmlString) {
    if (!editor) {
        return;
    }

    editor.value = htmlString;
    editor.synchronizeValues();
    rememberSelection();
    scheduleStateSync();
}

function GetHTMLContent() {
    return editor ? editor.value : '';
}

function SetLightEditor() {
    DarkReader.disable();
}

function SetDarkEditor() {
    DarkReader.enable();
}

function toggleToolbar(enable) {
    const toolbar = document.querySelector('.jodit-toolbar__box');
    if (toolbar) {
        toolbar.style.display = enable ? 'flex' : 'none';
    }

    scheduleStateSync();
}

function setSpellCheck(enable) {
    if (!editor || !editor.editor) {
        return;
    }

    const isEnabled = !!enable;
    editor.options.spellcheck = isEnabled;
    editor.editor.spellcheck = isEnabled;
    editor.editor.setAttribute('spellcheck', isEnabled ? 'true' : 'false');
    scheduleStateSync();
}

function insertImages(imagesInfo) {
    if (!editor) {
        return;
    }

    restoreEditorSelection();

    imagesInfo.forEach(imageInfo => {
        editor.selection.insertHTML(`<img src="${escapeHtmlAttribute(imageInfo.data)}" alt="${escapeHtmlAttribute(imageInfo.name)}">`);
    });

    rememberSelection();
    scheduleStateSync();
}

function focusEditor() {
    if (!editor) {
        return;
    }

    if (restoreEditorSelection()) {
        return;
    }

    editor.selection.focus();

    const lastChild = editor.editor.lastChild;
    if (lastChild) {
        editor.selection.setCursorIn(lastChild, false);
    }
}

function getEditorState() {
    return buildEditorState();
}

function executeEditorCommand(commandName) {
    if (!editor) {
        return;
    }

    restoreEditorSelection();
    editor.execCommand(commandName);
    rememberSelection();
    scheduleStateSync();
}

function setFontFamily(fontFamily) {
    applyInlineStyleToSelection({ fontFamily: `'${fontFamily}',Arial,sans-serif` });
}

function setFontSize(fontSize) {
    applyInlineStyleToSelection({ fontSize: `${fontSize}px` });
}

function setTextColor(color) {
    applyInlineStyleToSelection({ color: color || '' });
}

function setHighlightColor(color) {
    applyInlineStyleToSelection({ backgroundColor: color || '' });
}

function setParagraphStyle(tagName) {
    if (!editor) {
        return;
    }

    restoreEditorSelection();

    const normalizedTag = (tagName || 'div').toLowerCase();

    try {
        document.execCommand('formatBlock', false, normalizedTag);
    }
    catch {
        const block = getCurrentBlockElement();
        if (block && block.tagName.toLowerCase() !== normalizedTag) {
            const replacement = document.createElement(normalizedTag);
            while (block.firstChild) {
                replacement.appendChild(block.firstChild);
            }

            block.parentNode.replaceChild(replacement, block);
        }
    }

    rememberSelection();
    scheduleStateSync();
}

function setLineHeight(lineHeight) {
    restoreEditorSelection();

    const block = getCurrentBlockElement();
    if (!block) {
        return;
    }

    block.style.lineHeight = lineHeight || '';
    rememberSelection();
    scheduleStateSync();
}

function upsertLink(linkArgs) {
    if (!editor) {
        return;
    }

    restoreEditorSelection();

    const normalizedUrl = normalizeLinkUrl(linkArgs && linkArgs.url ? linkArgs.url : '');
    if (!normalizedUrl) {
        return;
    }

    const linkText = linkArgs && linkArgs.text ? linkArgs.text.trim() : '';
    const existingLink = getSelectionElement() ? getSelectionElement().closest('a[href]') : null;

    if (existingLink) {
        existingLink.setAttribute('href', normalizedUrl);
        if (linkArgs.openInNewWindow) {
            existingLink.setAttribute('target', '_blank');
            existingLink.setAttribute('rel', 'noopener noreferrer');
        }
        else {
            existingLink.removeAttribute('target');
            existingLink.removeAttribute('rel');
        }

        if (linkText) {
            existingLink.textContent = linkText;
        }

        rememberSelection();
        scheduleStateSync();
        return;
    }

    const selection = window.getSelection();
    if (selection && selection.rangeCount > 0 && !selection.isCollapsed && isSelectionInsideEditor()) {
        try {
            document.execCommand('createLink', false, normalizedUrl);
            const createdLink = getSelectionElement() ? getSelectionElement().closest('a[href]') : null;
            if (createdLink) {
                if (linkArgs.openInNewWindow) {
                    createdLink.setAttribute('target', '_blank');
                    createdLink.setAttribute('rel', 'noopener noreferrer');
                }
                if (linkText) {
                    createdLink.textContent = linkText;
                }
            }
        }
        catch {
            const selectedText = linkText || selection.toString() || normalizedUrl;
            editor.selection.insertHTML(`<a href="${escapeHtmlAttribute(normalizedUrl)}" target="_blank" rel="noopener noreferrer">${escapeHtmlText(selectedText)}</a>`);
        }

        rememberSelection();
        scheduleStateSync();
        return;
    }

    const text = linkText || normalizedUrl;
    editor.selection.insertHTML(`<a href="${escapeHtmlAttribute(normalizedUrl)}" target="_blank" rel="noopener noreferrer">${escapeHtmlText(text)}</a>`);
    rememberSelection();
    scheduleStateSync();
}

function removeLink() {
    restoreEditorSelection();

    const selectionElement = getSelectionElement();
    const linkElement = selectionElement ? selectionElement.closest('a[href]') : null;
    if (!linkElement) {
        return;
    }

    try {
        document.execCommand('unlink');
    }
    catch {
        unwrapElement(linkElement);
    }

    rememberSelection();
    scheduleStateSync();
}

function insertTableHtml(tableArgs) {
    if (!editor) {
        return;
    }

    restoreEditorSelection();

    const rows = clampInteger(tableArgs && tableArgs.rows, 1, 10);
    const columns = clampInteger(tableArgs && tableArgs.columns, 1, 10);
    const htmlRows = [];

    for (let rowIndex = 0; rowIndex < rows; rowIndex += 1) {
        const cells = [];
        for (let columnIndex = 0; columnIndex < columns; columnIndex += 1) {
            cells.push('<td style="border:1px solid #c7c7c7;padding:6px;min-width:32px;"><br></td>');
        }

        htmlRows.push(`<tr>${cells.join('')}</tr>`);
    }

    editor.selection.insertHTML(`<table style="border-collapse:collapse;width:100%;">${htmlRows.join('')}</table><div><br></div>`);
    rememberSelection();
    scheduleStateSync();
}

function bindImageInput() {
    if (imageInputBound) {
        return;
    }

    imageInput.addEventListener('change', () => {
        const file = imageInput.files[0];
        if (!file) {
            return;
        }

        const reader = new FileReader();
        reader.onload = event => {
            const base64Image = event.target.result;
            insertImages([{ data: base64Image, name: file.name }]);
            imageInput.value = '';
        };
        reader.readAsDataURL(file);
    });

    imageInputBound = true;
}

function bindEditorStateTracking() {
    if (!editor || !editor.editor) {
        return;
    }

    const syncHandler = () => {
        rememberSelection();
        scheduleStateSync();
    };

    ['keyup', 'mouseup', 'click', 'input', 'focus', 'blur'].forEach(eventName => {
        editor.editor.addEventListener(eventName, syncHandler);
    });

    if (editor.events && editor.events.on) {
        editor.events.on('afterSetMode change afterCommand', syncHandler);
    }

    editorDomObserver = new MutationObserver(() => scheduleStateSync());
    editorDomObserver.observe(editor.editor, {
        subtree: true,
        childList: true,
        attributes: true,
        characterData: true,
        attributeFilter: ['style', 'class', 'href', 'spellcheck']
    });

    selectionChangeHandler = () => {
        if (isSelectionInsideEditor()) {
            rememberSelection();
            scheduleStateSync();
        }
    };

    document.addEventListener('selectionchange', selectionChangeHandler);
}

function scheduleStateSync() {
    if (stateSyncQueued) {
        return;
    }

    stateSyncQueued = true;
    window.requestAnimationFrame(() => {
        stateSyncQueued = false;
        notifyState();
    });
}

function notifyState() {
    if (!window.chrome || !window.chrome.webview) {
        return;
    }

    window.chrome.webview.postMessage({
        type: 'state',
        state: buildEditorState()
    });
}

function buildEditorState() {
    const selectionElement = getSelectionElement();
    const contextElement = selectionElement || (editor && editor.editor ? editor.editor : document.body);
    const blockElement = getCurrentBlockElement() || contextElement;
    const style = window.getComputedStyle(contextElement);
    const blockStyle = window.getComputedStyle(blockElement);
    const selection = window.getSelection();
    const listElement = selectionElement ? selectionElement.closest('ol,ul') : null;
    const linkElement = selectionElement ? selectionElement.closest('a[href]') : null;
    const fontSize = parsePixelSize(style.fontSize);

    return {
        bold: queryCommandState('bold', isBoldStyle(style)),
        italic: queryCommandState('italic', style.fontStyle === 'italic'),
        underline: queryCommandState('underline', (style.textDecorationLine || '').includes('underline')),
        strikethrough: queryCommandState('strikeThrough', (style.textDecorationLine || '').includes('line-through')),
        orderedList: !!(listElement && listElement.tagName.toLowerCase() === 'ol'),
        unorderedList: !!(listElement && listElement.tagName.toLowerCase() === 'ul'),
        canIndent: queryCommandEnabled('indent', true),
        canOutdent: queryCommandEnabled('outdent', !!listElement || !!(selectionElement && selectionElement.closest('blockquote'))),
        hasSelection: !!(selection && selection.rangeCount > 0 && !selection.isCollapsed && isSelectionInsideEditor()),
        isSpellCheckEnabled: !!(editor && editor.editor && editor.editor.spellcheck),
        alignment: normalizeAlignment(blockStyle.textAlign),
        fontFamily: normalizeFontFamily(style.fontFamily),
        fontSize: fontSize,
        paragraphStyle: normalizeParagraphTag(blockElement),
        textColor: snapColorToPalette(resolveEditorColorValue(selectionElement, 'color', style.color), availableTextColors),
        highlightColor: snapColorToPalette(resolveEditorColorValue(selectionElement, 'backgroundColor', style.backgroundColor), availableHighlightColors),
        lineHeight: normalizeLineHeight(blockStyle.lineHeight, fontSize),
        linkUrl: linkElement ? linkElement.getAttribute('href') || '' : '',
        selectedText: selection && isSelectionInsideEditor() ? selection.toString() : ''
    };
}

function getSelectionElement() {
    if (!editor || !editor.editor) {
        return null;
    }

    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) {
        return editor.editor;
    }

    const node = selection.anchorNode;
    if (!node) {
        return editor.editor;
    }

    const element = node.nodeType === Node.TEXT_NODE ? node.parentElement : node;
    if (!element || !editor.editor.contains(element)) {
        return editor.editor;
    }

    return element;
}

function getCurrentBlockElement() {
    const selectionElement = getSelectionElement();
    if (!selectionElement) {
        return null;
    }

    return selectionElement.closest('h1,h2,h3,h4,h5,h6,p,blockquote,pre,div,li,td,th') || selectionElement;
}

function rememberSelection() {
    if (!editor || !editor.editor) {
        return;
    }

    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0 || !isSelectionInsideEditor()) {
        return;
    }

    try {
        lastKnownRange = selection.getRangeAt(0).cloneRange();
    }
    catch {
        lastKnownRange = null;
    }
}

function restoreEditorSelection() {
    if (!editor || !editor.editor) {
        return false;
    }

    editor.selection.focus();

    if (!lastKnownRange) {
        return false;
    }

    try {
        const selection = window.getSelection();
        if (!selection) {
            return false;
        }

        const restoredRange = lastKnownRange.cloneRange();
        selection.removeAllRanges();
        selection.addRange(restoredRange);
        return true;
    }
    catch {
        lastKnownRange = null;
        return false;
    }
}

function isSelectionInsideEditor() {
    if (!editor || !editor.editor) {
        return false;
    }

    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) {
        return false;
    }

    const anchorNode = selection.anchorNode;
    const focusNode = selection.focusNode;
    return !!anchorNode && !!focusNode && editor.editor.contains(anchorNode) && editor.editor.contains(focusNode);
}

function queryCommandState(commandName, fallbackValue) {
    try {
        const value = document.queryCommandState(commandName);
        return typeof value === 'boolean' ? value : fallbackValue;
    }
    catch {
        return fallbackValue;
    }
}

function queryCommandEnabled(commandName, fallbackValue) {
    try {
        const value = document.queryCommandEnabled(commandName);
        return typeof value === 'boolean' ? value : fallbackValue;
    }
    catch {
        return fallbackValue;
    }
}

function isBoldStyle(style) {
    const fontWeight = style.fontWeight || '400';
    const numericWeight = parseInt(fontWeight, 10);
    return fontWeight === 'bold' || Number.isFinite(numericWeight) && numericWeight >= 600;
}

function normalizeAlignment(value) {
    const normalized = (value || '').toLowerCase();
    if (normalized === 'center' || normalized === 'right' || normalized === 'justify') {
        return normalized;
    }

    return 'left';
}

function normalizeFontFamily(value) {
    if (!value) {
        return '';
    }

    return value.split(',')[0].replace(/["']/g, '').trim();
}

function normalizeParagraphTag(element) {
    return element && element.tagName ? element.tagName.toLowerCase() : 'div';
}

function normalizeLineHeight(value, fontSize) {
    if (!value || value === 'normal') {
        return 'normal';
    }

    const numericValue = parseFloat(value);
    if (!Number.isFinite(numericValue)) {
        return value;
    }

    if (value.endsWith('px') && fontSize) {
        const ratio = numericValue / fontSize;
        return Number.isInteger(ratio) ? `${ratio}` : ratio.toFixed(2).replace(/0+$/, '').replace(/\.$/, '');
    }

    return Number.isInteger(numericValue) ? `${numericValue}` : numericValue.toString();
}

function parsePixelSize(value) {
    const numericValue = parseFloat(value || '');
    return Number.isFinite(numericValue) ? Math.round(numericValue) : null;
}

function normalizeColor(value) {
    if (!value || value === 'transparent' || value === 'rgba(0, 0, 0, 0)') {
        return '';
    }

    if (value.startsWith('#')) {
        return value.toLowerCase();
    }

    const rgbaMatch = value.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)/i);
    if (!rgbaMatch) {
        return value.toLowerCase();
    }

    const [, red, green, blue] = rgbaMatch;
    return `#${toHex(red)}${toHex(green)}${toHex(blue)}`;
}

function snapColorToPalette(value, palette) {
    const normalizedColor = normalizeColor(value);
    if (!normalizedColor) {
        return '';
    }

    if (!Array.isArray(palette) || palette.length === 0) {
        return normalizedColor;
    }

    if (palette.includes(normalizedColor)) {
        return normalizedColor;
    }

    const targetRgb = hexToRgb(normalizedColor);
    if (!targetRgb) {
        return normalizedColor;
    }

    let nearestColor = palette[0];
    let nearestDistance = Number.MAX_SAFE_INTEGER;

    palette.forEach(candidate => {
        const candidateRgb = hexToRgb(candidate);
        if (!candidateRgb) {
            return;
        }

        const distance = getColorDistance(targetRgb, candidateRgb);
        if (distance < nearestDistance) {
            nearestColor = candidate;
            nearestDistance = distance;
        }
    });

    return nearestColor;
}

function hexToRgb(value) {
    const normalized = normalizeColor(value);
    if (!normalized || !normalized.startsWith('#') || normalized.length !== 7) {
        return null;
    }

    return {
        red: parseInt(normalized.slice(1, 3), 16),
        green: parseInt(normalized.slice(3, 5), 16),
        blue: parseInt(normalized.slice(5, 7), 16)
    };
}

function getColorDistance(left, right) {
    const redDiff = left.red - right.red;
    const greenDiff = left.green - right.green;
    const blueDiff = left.blue - right.blue;
    return (redDiff * redDiff) + (greenDiff * greenDiff) + (blueDiff * blueDiff);
}

function resolveEditorColorValue(selectionElement, propertyName, computedValue) {
    if (!editor || !editor.editor) {
        return '';
    }

    const darkReaderAttributeName = propertyName === 'backgroundColor'
        ? 'data-darkreader-inline-bgcolor'
        : 'data-darkreader-inline-color';

    let currentElement = selectionElement;
    while (currentElement) {
        if (currentElement.style && currentElement.style[propertyName]) {
            return currentElement.style[propertyName];
        }

        const darkReaderValue = currentElement.getAttribute && currentElement.getAttribute(darkReaderAttributeName);
        if (darkReaderValue) {
            return darkReaderValue;
        }

        if (currentElement === editor.editor) {
            break;
        }

        currentElement = currentElement.parentElement;
    }

    return '';
}

function toHex(value) {
    return Number(value).toString(16).padStart(2, '0');
}

function applyInlineStyleToSelection(styles) {
    if (!editor || !editor.editor) {
        return;
    }

    restoreEditorSelection();

    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0 || !isSelectionInsideEditor()) {
        return;
    }

    const range = selection.getRangeAt(0);
    if (selection.isCollapsed) {
        const contextElement = getSelectionElement();
        if (contextElement) {
            Object.entries(styles).forEach(([propertyName, propertyValue]) => {
                contextElement.style[propertyName] = propertyValue || '';
            });
        }

        rememberSelection();
        scheduleStateSync();
        return;
    }

    const span = document.createElement('span');
    Object.entries(styles).forEach(([propertyName, propertyValue]) => {
        if (propertyValue) {
            span.style[propertyName] = propertyValue;
        }
    });

    try {
        span.appendChild(range.extractContents());
        range.insertNode(span);
        selection.removeAllRanges();
        const newRange = document.createRange();
        newRange.selectNodeContents(span);
        selection.addRange(newRange);
    }
    catch {
        const css = styleObjectToCss(styles);
        const selectedText = escapeHtmlText(selection.toString());
        editor.selection.insertHTML(`<span style="${css}">${selectedText}</span>`);
    }

    rememberSelection();
    scheduleStateSync();
}

function styleObjectToCss(styles) {
    return Object.entries(styles)
        .filter(([, value]) => value)
        .map(([propertyName, propertyValue]) => `${camelToKebabCase(propertyName)}:${propertyValue}`)
        .join(';');
}

function camelToKebabCase(value) {
    return value.replace(/[A-Z]/g, match => `-${match.toLowerCase()}`);
}

function unwrapElement(element) {
    const parent = element.parentNode;
    if (!parent) {
        return;
    }

    while (element.firstChild) {
        parent.insertBefore(element.firstChild, element);
    }

    parent.removeChild(element);
}

function clampInteger(value, min, max) {
    const numericValue = parseInt(value, 10);
    if (!Number.isFinite(numericValue)) {
        return min;
    }

    return Math.min(max, Math.max(min, numericValue));
}

function normalizeLinkUrl(url) {
    const trimmed = (url || '').trim();
    if (!trimmed) {
        return '';
    }

    if (/^[a-z]+:/i.test(trimmed)) {
        return trimmed;
    }

    if (trimmed.includes('@') && !trimmed.includes('/')) {
        return `mailto:${trimmed}`;
    }

    return `https://${trimmed}`;
}

function escapeHtmlAttribute(value) {
    return `${value || ''}`
        .replace(/&/g, '&amp;')
        .replace(/"/g, '&quot;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;');
}

function escapeHtmlText(value) {
    return `${value || ''}`
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;');
}

