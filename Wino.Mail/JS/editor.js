const editor = Jodit.make("#editor", {
    "useSearch": false,
    "toolbar": true,
    "buttons": "bold,italic,underline,strikethrough,brush,ul,ol,font,fontsize,paragraph,image,link,indent,outdent,left",
    "inline": true,
    "toolbarAdaptive": false,
    "toolbarInlineForSelection": false,
    "showCharsCounter": false,
    "showWordsCounter": false,
    "showXPathInStatusbar": false,
    "disablePlugins": "add-new-line",
    "showPlaceholder": false,
    "uploader": {
        "insertImageAsBase64URI": true
    },
    "enter": "DIV",
    "minHeight": 200
});

// Handle the image input change event
imageInput.addEventListener('change', () => {
    const file = imageInput.files[0];
    if (file) {
        const reader = new FileReader();
        reader.onload = function (event) {
            const base64Image = event.target.result;
            editor.selection.insertHTML(`<img src="${base64Image}" alt="Embedded Image">`);
        };
        reader.readAsDataURL(file);
    }
});

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
    console.log(buttonContainer, ref, value);
    window.chrome.webview.postMessage({ type: ref, value: value });
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
