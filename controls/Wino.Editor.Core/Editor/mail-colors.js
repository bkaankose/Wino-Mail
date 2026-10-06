// Selective dark-mode color adaptation for mail HTML.
//
// Unlike a page-wide inverter, this engine keeps the sender's design wherever it can:
// - light neutral and pastel backgrounds become dark surfaces, keeping their hue;
// - saturated and already-dark backgrounds (buttons, banners, brand bars) stay as sent;
// - text is changed only when it no longer has readable contrast on its final background;
// - blocks that paint a background image or gradient are left completely untouched,
//   because the image determines the contrast of the text drawn over it;
// - images whose surroundings were darkened keep their original backdrop, so transparent
//   logos drawn in dark ink stay visible (the same trade-off Outlook makes).
// The engine only computes overrides. Callers decide how to apply and remove them.
(function () {
    "use strict";

    const minimumContrast = 4.5;
    // Spacer and tracking images are tiny; painting a backdrop would show them as light strips.
    const minimumBackdropImageSize = 8;
    const skippedTags = new Set([
        "IMG", "PICTURE", "VIDEO", "AUDIO", "CANVAS", "SVG", "MATH", "BR", "WBR",
        "STYLE", "SCRIPT", "TEMPLATE", "HEAD", "META", "TITLE", "SOURCE", "TRACK"
    ]);
    const parsedColors = new Map();
    let probeContext = null;

    function clamp(value, minimum, maximum) {
        return Math.min(maximum, Math.max(minimum, value));
    }

    function parseWithCanvas(value) {
        try {
            if (!probeContext) {
                const canvas = document.createElement("canvas");
                canvas.width = 1;
                canvas.height = 1;
                probeContext = canvas.getContext("2d", { willReadFrequently: true });
            }
            probeContext.clearRect(0, 0, 1, 1);
            probeContext.fillStyle = "rgba(0, 0, 0, 0)";
            probeContext.fillStyle = value;
            probeContext.fillRect(0, 0, 1, 1);
            const data = probeContext.getImageData(0, 0, 1, 1).data;
            return { r: data[0], g: data[1], b: data[2], a: data[3] / 255 };
        } catch (_) {
            return null;
        }
    }

    // Computed colors are usually rgb()/rgba(). Modern color spaces (oklch, lab, color())
    // keep their own syntax in computed style, so they are resolved through a canvas.
    function parseColor(value) {
        if (!value) return null;
        const text = String(value).trim();
        if (parsedColors.has(text)) return parsedColors.get(text);

        let color = null;
        if (text === "transparent") {
            color = { r: 0, g: 0, b: 0, a: 0 };
        } else {
            const match = /^rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)(?:\s*[,/]\s*([\d.]+)(%?))?\s*\)$/i.exec(text);
            if (match) {
                const alpha = match[4] === undefined
                    ? 1
                    : match[5] === "%" ? parseFloat(match[4]) / 100 : parseFloat(match[4]);
                color = { r: +match[1], g: +match[2], b: +match[3], a: clamp(alpha, 0, 1) };
            } else {
                color = parseWithCanvas(text);
            }
        }

        if (parsedColors.size > 4096) parsedColors.clear();
        parsedColors.set(text, color);
        return color;
    }

    function toHsl(color) {
        const r = color.r / 255;
        const g = color.g / 255;
        const b = color.b / 255;
        const max = Math.max(r, g, b);
        const min = Math.min(r, g, b);
        const l = (max + min) / 2;
        let h = 0;
        let s = 0;
        if (max !== min) {
            const d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max === r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max === g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return { h, s, l, a: color.a };
    }

    function fromHsl(hsl) {
        const s = clamp(hsl.s, 0, 1);
        const l = clamp(hsl.l, 0, 1);
        const c = (1 - Math.abs(2 * l - 1)) * s;
        const hp = ((hsl.h % 360) + 360) % 360 / 60;
        const x = c * (1 - Math.abs(hp % 2 - 1));
        let rgb;
        if (hp < 1) rgb = [c, x, 0];
        else if (hp < 2) rgb = [x, c, 0];
        else if (hp < 3) rgb = [0, c, x];
        else if (hp < 4) rgb = [0, x, c];
        else if (hp < 5) rgb = [x, 0, c];
        else rgb = [c, 0, x];
        const m = l - c / 2;
        return {
            r: Math.round((rgb[0] + m) * 255),
            g: Math.round((rgb[1] + m) * 255),
            b: Math.round((rgb[2] + m) * 255),
            a: hsl.a === undefined ? 1 : hsl.a
        };
    }

    function channel(value) {
        const v = value / 255;
        return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
    }

    function luminance(color) {
        return 0.2126 * channel(color.r) + 0.7152 * channel(color.g) + 0.0722 * channel(color.b);
    }

    function contrast(first, second) {
        const a = luminance(first);
        const b = luminance(second);
        return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
    }

    function blend(top, bottom) {
        const a = top.a;
        if (a >= 1) return { r: top.r, g: top.g, b: top.b, a: 1 };
        return {
            r: Math.round(top.r * a + bottom.r * (1 - a)),
            g: Math.round(top.g * a + bottom.g * (1 - a)),
            b: Math.round(top.b * a + bottom.b * (1 - a)),
            a: 1
        };
    }

    function serialize(color) {
        return color.a >= 1
            ? `rgb(${color.r}, ${color.g}, ${color.b})`
            : `rgba(${color.r}, ${color.g}, ${color.b}, ${Math.round(color.a * 1000) / 1000})`;
    }

    function isLightSurface(hsl) {
        // Pastels and light neutrals. Bright saturated colors (brand buttons, badges)
        // with lightness up to 0.8 are kept exactly as the sender chose them.
        return hsl.l > 0.55 && (hsl.s < 0.25 || hsl.l > 0.8);
    }

    function mapBackground(color, surface) {
        const hsl = toHsl(color);
        if (!isLightSurface(hsl)) return null;
        // White lands exactly on the reading surface, so full-width white layouts blend in.
        // Lighter originals stay darker than slightly tinted panels, keeping the hierarchy.
        const l = surface.l + (1 - hsl.l) * 0.35;
        const s = hsl.s < 0.25 ? Math.min(hsl.s, surface.s + 0.05) : Math.min(hsl.s, 0.5) * 0.6;
        return fromHsl({ h: hsl.h, s, l, a: hsl.a });
    }

    function mapBorder(color) {
        const hsl = toHsl(color);
        const neutral = hsl.s < 0.25;
        if (!neutral && hsl.l <= 0.8) return null;
        const l = 0.15 + (1 - hsl.l) * 0.5;
        const s = neutral ? hsl.s : Math.min(hsl.s, 0.5) * 0.6;
        return fromHsl({ h: hsl.h, s, l, a: hsl.a });
    }

    // The target never exceeds the contrast the sender's own pair had, so a color
    // combination that was designed that way (white on a bright red banner) stays untouched.
    function adjustText(color, background, originalBackground) {
        const opaque = color.a >= 1 ? color : blend(color, background);
        const original = color.a >= 1 ? color : blend(color, originalBackground);
        const target = Math.min(minimumContrast, contrast(original, originalBackground));
        if (contrast(opaque, background) >= target - 0.01) return null;

        const hsl = toHsl(color);
        const darkBackground = luminance(background) < 0.18;
        if (darkBackground && hsl.s < 0.25) {
            // Neutral body text keeps its relative weight: black becomes a soft white,
            // secondary grays stay a little dimmer than primary text.
            const candidate = fromHsl({ h: hsl.h, s: hsl.s, l: 0.88 - hsl.l * 0.3, a: 1 });
            if (contrast(candidate, background) >= target) return candidate;
        }

        let low = darkBackground ? hsl.l : 0;
        let high = darkBackground ? 1 : hsl.l;
        let best = fromHsl({ h: hsl.h, s: hsl.s, l: darkBackground ? 1 : 0, a: 1 });
        for (let step = 0; step < 14; step += 1) {
            const middle = (low + high) / 2;
            const candidate = fromHsl({ h: hsl.h, s: hsl.s, l: middle, a: 1 });
            if (contrast(candidate, background) >= target) {
                best = candidate;
                if (darkBackground) high = middle;
                else low = middle;
            } else if (darkBackground) {
                low = middle;
            } else {
                high = middle;
            }
        }
        return best;
    }

    const borderSides = ["top", "right", "bottom", "left"];

    // Returns [{ element, property, value }] that adapt `root` for a dark `surfaceColor`.
    function computeDarkOverrides(root, surfaceColor) {
        const surface = parseColor(surfaceColor) || { r: 18, g: 18, b: 18, a: 1 };
        const surfaceOpaque = { r: surface.r, g: surface.g, b: surface.b, a: 1 };
        const surfaceHsl = toHsl(surfaceOpaque);
        const overrides = [];

        function sameColor(first, second) {
            return first.r === second.r && first.g === second.g && first.b === second.b;
        }

        function keepImageBackdrop(image, background, originalBackground) {
            if (sameColor(background, originalBackground)) return;
            const bounds = image.getBoundingClientRect();
            if (Math.min(bounds.width, bounds.height) < minimumBackdropImageSize) return;
            const style = getComputedStyle(image);
            const own = parseColor(style.backgroundColor);
            if (style.display === "none" || (own && own.a > 0)) return;
            overrides.push({ element: image, property: "background-color", value: serialize(originalBackground) });
        }

        function visit(element, parentBackground, parentOriginalBackground) {
            const tagName = element.tagName.toUpperCase();
            if (tagName === "IMG") {
                keepImageBackdrop(element, parentBackground, parentOriginalBackground);
                return;
            }
            if (skippedTags.has(tagName)) return;
            const style = getComputedStyle(element);
            if (style.display === "none") return;

            // A background image or gradient decides the contrast for everything inside it.
            if (style.backgroundImage && style.backgroundImage !== "none") return;

            let background = parentBackground;
            let originalBackground = parentOriginalBackground;
            const ownBackground = parseColor(style.backgroundColor);
            if (ownBackground && ownBackground.a > 0) {
                originalBackground = blend(ownBackground, parentOriginalBackground);
                const mapped = mapBackground(ownBackground, surfaceHsl);
                if (mapped) {
                    overrides.push({ element, property: "background-color", value: serialize(mapped) });
                    background = blend(mapped, parentBackground);
                } else {
                    background = blend(ownBackground, parentBackground);
                }
            }

            const text = parseColor(style.color);
            if (text && text.a > 0) {
                const adjusted = adjustText(text, background, originalBackground);
                if (adjusted) overrides.push({ element, property: "color", value: serialize(adjusted) });
            }

            for (const side of borderSides) {
                if (style.getPropertyValue(`border-${side}-style`) === "none" ||
                    parseFloat(style.getPropertyValue(`border-${side}-width`)) <= 0) continue;
                const border = parseColor(style.getPropertyValue(`border-${side}-color`));
                if (!border || border.a === 0) continue;
                const mapped = mapBorder(border);
                if (mapped) overrides.push({ element, property: `border-${side}-color`, value: serialize(mapped) });
            }

            for (const child of element.children) visit(child, background, originalBackground);
        }

        // Mail is authored for a white page, so that is the original backdrop.
        visit(root, surfaceOpaque, { r: 255, g: 255, b: 255, a: 1 });
        return overrides;
    }

    window.WinoMailColors = {
        computeDarkOverrides,
        parseColor,
        contrast
    };
}());
