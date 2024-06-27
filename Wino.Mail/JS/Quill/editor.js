const quill = new Quill('#editor', {
    modules: {
        toolbar: '#toolbar-container'
    },
    placeholder: '',
    theme: 'snow'
});

var boldButton = document.getElementById('boldButton');
var italicButton = document.getElementById('italicButton');
var underlineButton = document.getElementById('underlineButton');
var strikeButton = document.getElementById('strikeButton');

var orderedListButton = document.getElementById('orderedListButton');
var bulletListButton = document.getElementById('bulletListButton');

var directionButton = document.getElementById('directionButton');

var alignLeftButton = document.getElementById('ql-align-left');
var alignCenterButton = document.getElementById('ql-align-center');
var alignRightButton = document.getElementById('ql-align-right');
var alignJustifyButton = document.getElementById('ql-align-justify');

// The mutation observer
var boldObserver = new MutationObserver(function () { classChanged(boldButton); });
boldObserver.observe(boldButton, { attributes: true, attributeFilter: ["class"] });

var italicObserver = new MutationObserver(function () { classChanged(italicButton); });
italicObserver.observe(italicButton, { attributes: true, attributeFilter: ["class"] });

var underlineObserver = new MutationObserver(function () { classChanged(underlineButton); });
underlineObserver.observe(underlineButton, { attributes: true, attributeFilter: ["class"] });

var strikeObserver = new MutationObserver(function () { classChanged(strikeButton); });
strikeObserver.observe(strikeButton, { attributes: true, attributeFilter: ["class"] });

var orderedListObserver = new MutationObserver(function () { classAndValueChanged(orderedListButton); });
orderedListObserver.observe(orderedListButton, { attributes: true, attributeFilter: ["class"] });

var bulletListObserver = new MutationObserver(function () { classAndValueChanged(bulletListButton); });
bulletListObserver.observe(bulletListButton, { attributes: true, attributeFilter: ["class"] });

var directionObserver = new MutationObserver(function () { classChanged(directionButton); });
directionObserver.observe(directionButton, { attributes: true, attributeFilter: ["class"] });

var alignmentObserver = new MutationObserver(function () { alignmentDataValueChanged(alignLeftButton); });
alignmentObserver.observe(alignLeftButton, { attributes: true, attributeFilter: ["class"] });

var alignmentObserverCenter = new MutationObserver(function () { alignmentDataValueChanged(alignCenterButton); });
alignmentObserverCenter.observe(alignCenterButton, { attributes: true, attributeFilter: ["class"] });

var alignmentObserverRight = new MutationObserver(function () { alignmentDataValueChanged(alignRightButton); });
alignmentObserverRight.observe(alignRightButton, { attributes: true, attributeFilter: ["class"] });

var alignmentObserverJustify = new MutationObserver(function () { alignmentDataValueChanged(alignJustifyButton); });
alignmentObserverJustify.observe(alignJustifyButton, { attributes: true, attributeFilter: ["class"] });

function classChanged(button) {
    window.chrome.webview.postMessage(`${button.className}`);
}

function classAndValueChanged(button) {
    window.chrome.webview.postMessage(`${button.id} ${button.className}`);
}

function alignmentDataValueChanged(button) {
    if (button.className.endsWith('ql-active'))
        window.chrome.webview.postMessage(`${button.id}`);
}

function RenderHTML(htmlString) {
    const delta = quill.clipboard.convert({html: htmlString})

    quill.setContents(delta, 'silent');
}

function GetHTMLContent() {
    return quill.root.innerHTML;
}

function GetTextContent() {
    return quill.getText();
}

function SetLightEditor() {
    DarkReader.disable();
}

function SetDarkEditor() {
    DarkReader.enable();
}

function getSelectedText() {
    var range = quill.getSelection();
    if (range) {
        if (range.length == 0) {

        }
        else {
            return quill.getText(range.index, range.length);
        }
    }
}

function addHyperlink(url) {
    var range = quill.getSelection();

    if (range) {
        quill.formatText(range.index, range.length, 'link', url);
        quill.setSelection(0, 0);
    }
}
