// Pure color-policy tests; no browser or application UI automation.
// Run: node --test tests/Wino.Mail.Controls.Tests/JavaScript/mail-colors.test.cjs
const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const { test } = require("node:test");
const vm = require("node:vm");

const source = readFileSync(path.join(__dirname,
    "../../../controls/Wino.Editor.Core/Editor/mail-colors.js"), "utf8");
const context = vm.createContext({ window: {}, getComputedStyle: element => element.computed });
vm.runInContext(source, context);
const { computeDarkOverrides, parseColor, contrast } = context.window.WinoMailColors;
const surface = "rgb(18, 18, 18)";

// Computed styles are the input boundary of the policy. Inherited and legacy font
// colors arrive here resolved to RGB by the browser, just like inline CSS colors.
function element(tagName, properties = {}, children = []) {
    return {
        tagName,
        children,
        computed: {
            display: "block",
            backgroundImage: "none",
            backgroundColor: "rgba(0, 0, 0, 0)",
            color: "rgb(0, 0, 0)",
            getPropertyValue: name => name.endsWith("-style") ? "none" : "0px",
            ...properties
        },
        getBoundingClientRect: () => ({ width: 100, height: 40 })
    };
}

function compose(children) {
    return element("DIV", { backgroundColor: surface, color: "rgb(243, 243, 243)" }, children);
}

function overrideFor(overrides, target, property) {
    return overrides.find(item => item.element === target && item.property === property)?.value;
}

function assertReadable(overrides, target, background = surface) {
    const color = overrideFor(overrides, target, "color") || target.computed.color;
    assert.ok(contrast(parseColor(color), parseColor(background)) >= 4.5,
        `${target.tagName}: ${color} must be readable on ${background}`);
}

test("transparent quoted headers, bold labels and legacy font colors adapt on a dark canvas", () => {
    const label = element("B");
    const legacy = element("FONT");
    const link = element("A", { color: "rgb(0, 0, 238)" });
    const header = element("DIV", {}, [label, legacy, link]);
    const root = compose([header]);
    const overrides = computeDarkOverrides(root, surface, { rootIsSurface: true });

    for (const target of [header, label, legacy, link]) assertReadable(overrides, target);
    assert.equal(overrideFor(overrides, root, "background-color"), undefined);
});

test("explicit light panels inside the composer still adapt their own background and text", () => {
    const panel = element("DIV", { backgroundColor: "rgb(255, 255, 255)" });
    const overrides = computeDarkOverrides(compose([panel]), surface, { rootIsSurface: true });
    const background = overrideFor(overrides, panel, "background-color");

    assert.equal(background, surface);
    assertReadable(overrides, panel, background);
});

test("authored dark and saturated banners retain their colors", () => {
    const dark = element("DIV", { backgroundColor: "rgb(10, 10, 10)", color: "rgb(255, 255, 255)" });
    const brand = element("DIV", { backgroundColor: "rgb(11, 61, 145)", color: "rgb(255, 255, 255)" });
    const overrides = computeDarkOverrides(compose([dark, brand]), surface, { rootIsSurface: true });

    assert.equal(overrides.length, 0);
});

test("gradient content remains untouched", () => {
    const child = element("B");
    const panel = element("DIV", { backgroundImage: "linear-gradient(red, yellow)" }, [child]);
    const overrides = computeDarkOverrides(compose([panel]), surface, { rootIsSurface: true });

    assert.equal(overrides.length, 0);
});

test("images preserve their original light backdrop and tiny tracking images do not gain one", () => {
    const image = element("IMG");
    const tracker = element("IMG");
    tracker.getBoundingClientRect = () => ({ width: 1, height: 1 });
    const overrides = computeDarkOverrides(compose([image, tracker]), surface, { rootIsSurface: true });

    assert.equal(overrideFor(overrides, image, "background-color"), "rgb(255, 255, 255)");
    assert.equal(overrideFor(overrides, tracker, "background-color"), undefined);
});

test("reader roots retain authored background handling by default", () => {
    const child = element("P");
    const root = element("HTML", { backgroundColor: "rgb(255, 255, 255)" }, [child]);
    const overrides = computeDarkOverrides(root, surface);

    assert.equal(overrideFor(overrides, root, "background-color"), surface);
    assertReadable(overrides, child);

    root.computed.backgroundColor = surface;
    root.computed.color = child.computed.color = "rgb(255, 255, 255)";
    assert.equal(computeDarkOverrides(root, surface).length, 0);
});

test("repeated computations are deterministic and never mutate authored colors", () => {
    const header = element("DIV", {}, [element("B")]);
    const root = compose([header]);
    const original = JSON.stringify(root);
    const first = computeDarkOverrides(root, surface, { rootIsSurface: true });

    for (let iteration = 0; iteration < 5; iteration++) {
        assert.deepEqual(computeDarkOverrides(root, surface, { rootIsSurface: true }), first);
        assert.equal(JSON.stringify(root), original);
    }
});
