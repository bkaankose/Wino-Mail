(function () {
    "use strict";

    const editor = document.getElementById("wino-editor");
    const overlay = document.getElementById("wino-overlay");
    const directions = ["nw", "n", "ne", "e", "se", "s", "sw", "w"];
    let selectedImage = null;
    let frame = null;
    let handles = [];
    let resize = null;

    function escapeAttribute(value) {
        return String(value)
            .replace(/&/g, "&amp;")
            .replace(/"/g, "&quot;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;");
    }

    function clearOverlay() {
        if (frame) frame.remove();
        handles.forEach(handle => handle.remove());
        frame = null;
        handles = [];
    }

    function clearSelection() {
        selectedImage = null;
        resize = null;
        clearOverlay();
        window.WinoEditor.notifySelectionChanged();
    }

    function handlePosition(direction, rect) {
        const x = direction.includes("w") ? rect.left : direction.includes("e") ? rect.right : rect.left + rect.width / 2;
        const y = direction.includes("n") ? rect.top : direction.includes("s") ? rect.bottom : rect.top + rect.height / 2;
        return { x, y };
    }

    function updateOverlay() {
        if (!selectedImage || !document.documentElement.contains(selectedImage)) {
            if (selectedImage) clearSelection();
            return;
        }

        const rect = selectedImage.getBoundingClientRect();
        if (!frame) {
            frame = document.createElement("div");
            frame.className = "wino-image-frame";
            overlay.appendChild(frame);

            handles = directions.map(direction => {
                const handle = document.createElement("div");
                handle.className = `wino-resize-handle wino-${direction}`;
                handle.dataset.direction = direction;
                handle.addEventListener("pointerdown", beginResize);
                overlay.appendChild(handle);
                return handle;
            });
        }

        frame.style.left = `${rect.left}px`;
        frame.style.top = `${rect.top}px`;
        frame.style.width = `${rect.width}px`;
        frame.style.height = `${rect.height}px`;

        handles.forEach(handle => {
            const position = handlePosition(handle.dataset.direction, rect);
            handle.style.left = `${position.x - 5}px`;
            handle.style.top = `${position.y - 5}px`;
        });
    }

    function selectImage(image) {
        selectedImage = image;
        updateOverlay();
        window.WinoEditor.notifySelectionChanged();
    }

    function selectedProperties() {
        if (!selectedImage || !document.documentElement.contains(selectedImage)) {
            return null;
        }

        const anchor = selectedImage.closest("a");
        return {
            altText: selectedImage.alt || "",
            linkUrl: anchor ? anchor.href : null
        };
    }

    function setSelectedProperties(properties) {
        if (!selectedImage || !document.documentElement.contains(selectedImage)) {
            return false;
        }

        selectedImage.alt = String(properties && properties.altText || "");
        const linkUrl = String(properties && properties.linkUrl || "").trim();
        const openInNewWindow = !properties || properties.openInNewWindow !== false;
        const currentAnchor = selectedImage.closest("a");

        if (linkUrl) {
            const anchor = currentAnchor || document.createElement("a");
            anchor.href = linkUrl;
            if (openInNewWindow) {
                anchor.target = "_blank";
                anchor.rel = "noopener noreferrer";
            } else {
                anchor.removeAttribute("target");
                anchor.removeAttribute("rel");
            }

            if (!currentAnchor) {
                selectedImage.replaceWith(anchor);
                anchor.appendChild(selectedImage);
            }
        } else if (currentAnchor) {
            currentAnchor.replaceWith(selectedImage);
        }

        updateOverlay();
        window.WinoEditor.notifyContentChanged();
        window.WinoEditor.notifySelectionChanged();
        return true;
    }

    function beginResize(event) {
        if (!selectedImage) return;
        event.preventDefault();
        event.stopPropagation();
        const rect = selectedImage.getBoundingClientRect();
        resize = {
            direction: event.currentTarget.dataset.direction,
            startX: event.clientX,
            startY: event.clientY,
            width: rect.width,
            height: rect.height,
            left: rect.left,
            top: rect.top
        };
        event.currentTarget.setPointerCapture(event.pointerId);
    }

    function ratioSize(direction, deltaX, deltaY) {
        const horizontalScale = direction.includes("e")
            ? (resize.width + deltaX) / resize.width
            : direction.includes("w")
                ? (resize.width - deltaX) / resize.width
                : null;
        const verticalScale = direction.includes("s")
            ? (resize.height + deltaY) / resize.height
            : direction.includes("n")
                ? (resize.height - deltaY) / resize.height
                : null;
        let scale;
        if (horizontalScale !== null && verticalScale !== null) {
            scale = Math.abs(horizontalScale - 1) >= Math.abs(verticalScale - 1) ? horizontalScale : verticalScale;
        } else {
            scale = horizontalScale !== null ? horizontalScale : verticalScale !== null ? verticalScale : 1;
        }
        scale = Math.max(16 / resize.width, 16 / resize.height, scale);
        return { width: resize.width * scale, height: resize.height * scale };
    }

    function resizeImage(event) {
        if (!resize || !selectedImage) return;
        const direction = resize.direction;
        const deltaX = event.clientX - resize.startX;
        const deltaY = event.clientY - resize.startY;
        let width = resize.width;
        let height = resize.height;

        if (event.shiftKey) {
            ({ width, height } = ratioSize(direction, deltaX, deltaY));
        } else {
            if (direction.includes("e")) width = resize.width + deltaX;
            if (direction.includes("w")) width = resize.width - deltaX;
            if (direction.includes("s")) height = resize.height + deltaY;
            if (direction.includes("n")) height = resize.height - deltaY;
            width = Math.max(16, width);
            height = Math.max(16, height);
        }

        selectedImage.style.width = `${Math.round(width)}px`;
        selectedImage.style.height = `${Math.round(height)}px`;
        selectedImage.style.maxWidth = "none";
        updateOverlay();
    }

    function finishResize() {
        if (!resize) return;
        resize = null;
        window.WinoEditor.notifyContentChanged();
    }

    function rangeFromPoint(x, y) {
        if (document.caretRangeFromPoint) {
            const range = document.caretRangeFromPoint(x, y);
            return range && editor.contains(range.commonAncestorContainer) ? range : null;
        }
        return null;
    }

    function insertImage(dataUri, range) {
        if (!dataUri || !String(dataUri).startsWith("data:image/")) return false;
        const html = `<img src="${escapeAttribute(dataUri)}" alt="" style="max-width:100%;height:auto;">`;
        const result = window.WinoEditor.insertHtml(html, range);
        const selection = window.getSelection();
        const node = selection && selection.anchorNode;
        const element = node && (node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement);
        const image = element && (element.previousElementSibling instanceof HTMLImageElement
            ? element.previousElementSibling
            : element.closest && element.closest("img"));
        if (image) selectImage(image);
        return result;
    }

    function readImageFile(file, range) {
        if (!file || !String(file.type).startsWith("image/")) return;
        const reader = new FileReader();
        reader.addEventListener("load", () => insertImage(reader.result, range));
        reader.readAsDataURL(file);
    }

    editor.addEventListener("click", event => {
        if (event.target instanceof HTMLImageElement) {
            event.preventDefault();
            selectImage(event.target);
        } else {
            clearSelection();
        }
    });

    editor.addEventListener("dragover", event => {
        if (event.dataTransfer && Array.from(event.dataTransfer.items).some(item => item.kind === "file" && item.type.startsWith("image/"))) {
            event.preventDefault();
            event.dataTransfer.dropEffect = "copy";
        }
    });

    editor.addEventListener("drop", event => {
        const files = event.dataTransfer && Array.from(event.dataTransfer.files).filter(file => file.type.startsWith("image/"));
        if (!files || files.length === 0) return;
        event.preventDefault();
        let range = rangeFromPoint(event.clientX, event.clientY);
        files.forEach(file => {
            readImageFile(file, range);
            range = null;
        });
    });

    editor.addEventListener("paste", event => {
        if (event.defaultPrevented) return;
        const files = event.clipboardData && Array.from(event.clipboardData.files).filter(file => file.type.startsWith("image/"));
        if (!files || files.length === 0) return;
        event.preventDefault();
        files.forEach(file => readImageFile(file));
    });

    document.addEventListener("keydown", event => {
        if (!selectedImage || (event.key !== "Delete" && event.key !== "Backspace")) return;
        event.preventDefault();
        selectedImage.remove();
        clearSelection();
        window.WinoEditor.notifyContentChanged();
    }, true);

    window.addEventListener("pointermove", resizeImage, true);
    window.addEventListener("pointerup", finishResize, true);
    window.addEventListener("pointercancel", finishResize, true);
    window.addEventListener("resize", updateOverlay);
    editor.addEventListener("scroll", updateOverlay, { passive: true });
    editor.addEventListener("input", updateOverlay);

    window.WinoEditorImages = {
        insertImage,
        clearSelection,
        isSelected() {
            return Boolean(selectedImage && document.documentElement.contains(selectedImage));
        },
        getSelectedProperties: selectedProperties,
        setSelectedProperties
    };
}());
