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
    "disablePlugins": "add-new-line,backspace",
    "showPlaceholder": false,
    "uploader": {
        "insertImageAsBase64URI": true
    },
    "enter": "DIV"
}

// This method should be called first all the time. 
function initializeJodit(fonts, defaultComposerFont, defaultComposerFontSize, defaultReaderFont, defaultReaderFontSize) {
    const fontsWithFallabckObject = fonts.reduce((acc, font) => { acc[`'${font}',Arial,sans-serif`] = font; return acc; }, {});
    const mergedConfig = {
        ...joditConfig,
        controls: {
            font: {
                list: Jodit.atom(fontsWithFallabckObject)
            }
        },
        style: { font: `${defaultReaderFontSize}px ${defaultReaderFont}` },
    }

    Jodit.plugins.add('inlineFonts', jodit => {
        jodit.events.on('afterEnter', e => {
            const current = jodit.selection.current().parentNode;
            current.style.fontFamily = `'${defaultComposerFont}',Arial,sans-serif`;
            current.style.fontSize = `${defaultComposerFontSize}px`;
        });
    });

    // Don't add const/let/var here, it should be global
    editor = Jodit.make("#editor", mergedConfig);

    // Handle the image input change event
    imageInput.addEventListener('change', () => {
        const file = imageInput.files[0];
        if (file) {
            const reader = new FileReader();
            reader.onload = function (event) {
                const base64Image = event.target.result;
                insertImages([{ data: base64Image, name: file.name }]);
            };
            reader.readAsDataURL(file);
        }
    });

    // Listeners for button events
    const disabledButtons = ["indent", "outdent"];
    const ariaPressedButtons = ["bold", "italic", "underline", "strikethrough", "ul", "ol"];

    const alignmentButton = document.querySelector(`[ref='left']`).firstChild.firstChild;
    const alignmentObserver = new MutationObserver(function () {
        const value = alignmentButton.firstChild.getAttribute('class').split(' ')[0];
        window.chrome.webview.postMessage({ type: 'alignment', value: value });
    });
    alignmentObserver.observe(alignmentButton, { childList: true, attributes: true, attributeFilter: ["class"] });

    const ariaObservers = ariaPressedButtons.map(button => {
        const buttonContainer = document.querySelector(`[ref='${button}']`);
        const observer = new MutationObserver(function () { pressedChanged(buttonContainer) });
        observer.observe(buttonContainer.firstChild, { attributes: true, attributeFilter: ["aria-pressed"] });

        return observer;
    });

    const disabledObservers = disabledButtons.map(button => {
        const buttonContainer = document.querySelector(`[ref='${button}']`);
        const observer = new MutationObserver(function () { disabledButtonChanged(buttonContainer) });
        observer.observe(buttonContainer.firstChild, { attributes: true, attributeFilter: ["disabled"] });

        return observer;
    });

    function pressedChanged(buttonContainer) {
        const ref = buttonContainer.getAttribute('ref');
        const value = buttonContainer.firstChild.getAttribute('aria-pressed');
        window.chrome.webview.postMessage({ type: ref, value: value });
    }

    function disabledButtonChanged(buttonContainer) {
        const ref = buttonContainer.getAttribute('ref');
        const value = buttonContainer.firstChild.getAttribute('disabled');
        window.chrome.webview.postMessage({ type: ref, value: value });
    }
}

function RenderHTML(htmlString) {
    editor.s.insertHTML(htmlString);
    editor.synchronizeValues();
}

function GetHTMLContent() {
    return editor.value;
}

function SetLightEditor() {
    DarkReader.disable();
}

function SetDarkEditor() {
    DarkReader.enable();
}

function toggleToolbar(enable) {
    const toolbar = document.querySelector('.jodit-toolbar__box');
    if (enable == 'true') {
        toolbar.style.display = 'flex';
    }
    else {
        toolbar.style.display = 'none';
    }
}

function insertImages(imagesInfo) {
    imagesInfo.forEach(imageInfo => {
        editor.selection.insertHTML(`<img src="${imageInfo.data}" alt="${imageInfo.name}">`);
    });
};
